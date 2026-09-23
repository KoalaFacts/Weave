use serde_json::{json, Value};
use std::io::{self, BufRead, Write};

fn error(id: Value, code: i32, message: &str) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "error": {"code": code, "message": message}})
}

fn handle(message: Value, initialized: &mut bool, ready: &mut bool) -> Option<Value> {
    if !message.is_object() || message["jsonrpc"] != "2.0" || !message["method"].is_string() {
        return Some(error(Value::Null, -32600, "Invalid request."));
    }
    let method = message["method"].as_str().unwrap();
    let Some(id) = message.get("id") else {
        if method == "notifications/initialized" && *initialized {
            *ready = true;
        }
        return None;
    };
    if !(id.is_string() || id.is_i64() || id.is_u64()) {
        return Some(error(Value::Null, -32600, "Invalid request ID."));
    }
    let empty = json!({});
    let params = message.get("params").unwrap_or(&empty);
    if !params.is_object() {
        return Some(error(id.clone(), -32602, "Expected object parameters."));
    }
    let result = match method {
        "initialize" => {
            *initialized = true;
            json!({"protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                "serverInfo": {"name": "echo-plugin", "version": "0.1.0"}})
        }
        "ping" => json!({}),
        _ if !*ready => return Some(error(id.clone(), -32002, "Server not initialized.")),
        "tools/list" => {
            json!({"tools": [{"name": "echo", "description": "Return the supplied text unchanged.",
            "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}},
                "required": ["text"], "additionalProperties": false}}]})
        }
        "tools/call" => {
            if params["name"] != "echo" {
                return Some(error(id.clone(), -32602, "Unknown tool."));
            }
            let args = &params["arguments"];
            let valid = args.as_object().is_some_and(|a| a.len() == 1) && args["text"].is_string();
            // Replace this expression with your own tool's behavior.
            let text = if valid {
                args["text"].as_str().unwrap()
            } else {
                "Expected exactly one string argument: text."
            };
            json!({"content": [{"type": "text", "text": text}], "isError": !valid})
        }
        _ => return Some(error(id.clone(), -32601, "Unknown method.")),
    };
    Some(json!({"jsonrpc": "2.0", "id": id, "result": result}))
}

fn main() -> io::Result<()> {
    let (mut initialized, mut ready) = (false, false);
    let mut stdout = io::stdout().lock();
    for line in io::stdin().lock().lines() {
        let response = match serde_json::from_str(&line?) {
            Ok(message) => handle(message, &mut initialized, &mut ready),
            Err(_) => Some(error(Value::Null, -32700, "Invalid JSON.")),
        };
        if let Some(response) = response {
            writeln!(stdout, "{response}")?;
            stdout.flush()?;
        }
    }
    Ok(())
}
