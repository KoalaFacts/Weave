# Copied from Nous Research Hermes Agent, plugins/platforms/slack/adapter.py
# Revision 4ae451d4126327c871fd084e60c045dec0c85911.
#
# MIT License
# Copyright (c) 2025 Nous Research
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

from typing import Any, Optional


def _slack_response_payload(response: Any) -> dict[str, Any]:
    """Return a Slack Web API response as a plain dict (``{}`` for unknown shapes).
    ``SlackResponse`` is mapping-like but not a ``dict``, so an ``isinstance(resp, dict)`` gate is
    always False at runtime and silently degrades results; normalize here instead."""
    if isinstance(response, dict):
        return response
    data = getattr(response, "data", None)
    return data if isinstance(data, dict) else {}


def _standalone_post_kwargs(
    chat_id: str, text: Any, unfurl_kwargs: dict[str, Any], thread_id: Optional[str]
) -> dict[str, Any]:
    """``chat.postMessage`` kwargs for the standalone senders (key order is the wire order)."""
    kwargs: dict[str, Any] = {"channel": chat_id, "text": text, "mrkdwn": True, **unfurl_kwargs}
    if thread_id:
        kwargs["thread_ts"] = thread_id
    return kwargs


async def _standalone_post_text(
    client, chat_id: str, text: Any, unfurl_kwargs: dict[str, Any], thread_id: Optional[str]
) -> dict[str, Any]:
    """``chat.postMessage`` via the SDK client; returns the response as a plain dict."""
    kwargs = _standalone_post_kwargs(chat_id, text, unfurl_kwargs, thread_id)
    return _slack_response_payload(await client.chat_postMessage(**kwargs))
