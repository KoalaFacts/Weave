"""Private service for Hermes outbound primitives called after Weave owner approval."""

import json
import re

from js import AbortSignal, Object, fetch
from pyodide.ffi import to_js
from workers import Response, WorkerEntrypoint

from slack_sender import _standalone_post_text

SLACK_POST = "https://slack.com/api/chat.postMessage"
SLACK_OPTIONS = {"parse": "none", "unfurl_links": False, "unfurl_media": False}
MAX_RESPONSE_BYTES = 64 * 1024


async def read_slack_answer(response):
    reader = response.body.getReader()
    body = bytearray()
    try:
        while True:
            chunk = await reader.read()
            if chunk.done:
                return json.loads(body.decode("utf-8"))
            if len(body) + chunk.value.length > MAX_RESPONSE_BYTES:
                raise ValueError("Slack response exceeded the size limit")
            body.extend(chunk.value.to_py())
    finally:
        await reader.cancel()


class SlackTransport:
    """Only the SDK method used by the upstream sender, without SDK retries."""

    def __init__(self, authorization):
        self.authorization = authorization
        self.status = 502

    async def chat_postMessage(self, **payload):
        response = await fetch(SLACK_POST, to_js({
            "method": "POST",
            "redirect": "manual",
            "signal": AbortSignal.timeout(12_000),
            "headers": {"authorization": self.authorization, "content-type": "application/json"},
            "body": json.dumps(payload),
        }, dict_converter=Object.fromEntries))
        self.status = response.status
        if self.status == 200 and response.body is not None:
            return await read_slack_answer(response)
        if response.body is not None:
            await response.body.cancel()
        return {}


class Default(WorkerEntrypoint):
    async def fetch(self, request):
        if request.method != "POST" or request.url != "https://hermes.internal/slack/text":
            return Response(None, status=404)
        authorization = request.headers.get("authorization") or ""
        if not re.fullmatch(r"Bearer xoxb-[A-Za-z0-9-]{1,2043}", authorization):
            return Response(None, status=400)
        try:
            message = await request.json()
        except ValueError:
            return Response(None, status=400)
        if not isinstance(message, dict):
            return Response(None, status=400)
        channel, text = message.get("channel"), message.get("text")
        if not isinstance(channel, str) or not re.fullmatch(r"[CG][A-Z0-9]{1,24}", channel):
            return Response(None, status=400)
        if not isinstance(text, str) or not text or len(text) > 20_000:
            return Response(None, status=400)
        transport = SlackTransport(authorization)
        try:
            result = await _standalone_post_text(transport, channel, text, SLACK_OPTIONS, None)
        except Exception:
            # The request may have reached Slack. The caller records an unknown outcome.
            return Response(None, status=502)
        if transport.status != 200:
            # Only statuses that Weave explicitly recognizes can be returned as refusals.
            return Response(None, status=transport.status if transport.status in {401, 403, 429} else 502)
        return Response.json({key: result[key] for key in ("ok", "channel", "ts", "error") if key in result})
