use reqwest::{blocking::Client as HttpClient, header::{HeaderMap, HeaderValue}, Method, Url};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{collections::BTreeMap, fmt, io::Read, time::Duration};

pub const MAX_REQUEST_BYTES: usize = 1_048_576;
const MAX_RESPONSE_BYTES: u64 = 8_388_608;

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Invocation {
    pub invocation_id: String,
    pub tool_name: String,
    pub method: String,
    pub parameters: BTreeMap<String, String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub raw_input: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientError {
    pub kind: &'static str,
    pub invocation_id: Option<String>,
    pub delivery: &'static str,
}
impl ClientError {
    pub fn invalid() -> Self { Self {kind:"invalid-input", invocation_id:None, delivery:"not-sent"} }
    fn unconfirmed(kind: &'static str, id: &str) -> Self { Self {kind, invocation_id:Some(id.into()), delivery:"unconfirmed"} }
}
impl fmt::Display for ClientError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}; retain the original invocation ID. No automatic retry.", self.kind)
    }
}
impl std::error::Error for ClientError {}

#[derive(Serialize)]
pub struct Reply { pub status: u16, pub body: Value }

pub fn valid_component(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && value != "." && value != ".."
        && value.bytes().all(|c| c.is_ascii_alphanumeric() || b"_.-".contains(&c))
}
pub fn canonical_id(value: &str) -> Result<String, ClientError> {
    if value.len() != 32 || !value.bytes().all(|c|c.is_ascii_hexdigit()) || value.bytes().all(|c|c == b'0') {
        return Err(ClientError::invalid());
    }
    Ok(value.to_ascii_lowercase())
}
fn base_url(value: &str) -> Result<String, ClientError> {
    let url = Url::parse(value).map_err(|_|ClientError::invalid())?;
    let raw_path = value.split_once("://").and_then(|(_,rest)|rest.split_once('/')).map(|(_,p)|p).unwrap_or("");
    if value.bytes().any(|c|c <= b' ' || c == 127 || c == b'\\')
        || !url.username().is_empty() || url.password().is_some() || url.query().is_some() || url.fragment().is_some()
        || !matches!(url.scheme(), "https"|"http") || url.host_str().is_none()
        || (url.scheme()=="http" && !matches!(url.host_str(),Some("127.0.0.1"|"[::1]")))
        || !url.path().bytes().all(|c|c.is_ascii_alphanumeric()||b"._~/-".contains(&c))
        || raw_path.split('/').any(|c|c=="."||c=="..") {
        return Err(ClientError::invalid());
    }
    Ok(url.as_str().trim_end_matches('/').into())
}

/// Agent-side client; authority and approval remain server-owned. No automatic IDs or retries.
pub struct Client { http: HttpClient, base: String, workspace: String }
impl Client {
    pub fn new(base: &str, workspace: &str, capability: &str, global_bearer: Option<&str>, timeout_ms: u64) -> Result<Self,ClientError> {
        let base=base_url(base)?;
        if !valid_component(workspace) || capability.is_empty() || capability.len()>16_384
            || !capability.bytes().all(|c|c.is_ascii_alphanumeric()||b"_-".contains(&c)) || !(1..=300_000).contains(&timeout_ms) {
            return Err(ClientError::invalid());
        }
        let mut headers=HeaderMap::new();
        let mut credential=HeaderValue::from_str(capability).map_err(|_|ClientError::invalid())?;
        credential.set_sensitive(true);
        headers.insert("X-Weave-Capability",credential);
        headers.insert("Accept",HeaderValue::from_static("application/json"));
        if let Some(bearer)=global_bearer {
            if bearer.is_empty() || bearer.len()>16_384 || !bearer.bytes().all(|c|(33..=126).contains(&c)) {return Err(ClientError::invalid());}
            let mut header=HeaderValue::from_str(&format!("Bearer {bearer}")).map_err(|_|ClientError::invalid())?;
            header.set_sensitive(true); headers.insert("Authorization",header);
        }
        let http=HttpClient::builder().default_headers(headers).no_proxy()
            .redirect(reqwest::redirect::Policy::none()).retry(reqwest::retry::never())
            .referer(false).http1_only().pool_max_idle_per_host(0)
            .timeout(Duration::from_millis(timeout_ms)).build().map_err(|_|ClientError::invalid())?;
        Ok(Self {http,base,workspace:workspace.into()})
    }
    pub fn invoke(&self, request: &Invocation) -> Result<Reply,ClientError> {
        let mut owned=request.clone(); owned.invocation_id=canonical_id(&owned.invocation_id)?;
        if !valid_component(&owned.tool_name) || owned.method.trim().is_empty() {return Err(ClientError::invalid());}
        let body=serde_json::to_vec(&owned).map_err(|_|ClientError::invalid())?;
        if body.len()>MAX_REQUEST_BYTES {return Err(ClientError::invalid());}
        self.send(&owned.tool_name,&owned.invocation_id,"",Some(body),false)
    }
    pub fn get_invocation(&self, tool: &str, id: &str) -> Result<Reply,ClientError> {
        let id=canonical_id(id)?; self.send(tool,&id,&format!("/{id}"),None,false)
    }
    pub fn get_approval(&self, tool: &str, id: &str) -> Result<Reply,ClientError> {
        let id=canonical_id(id)?; self.send(tool,&id,&format!("/{id}/approval"),None,true)
    }
    fn send(&self, tool: &str, id: &str, suffix: &str, body: Option<Vec<u8>>, approval: bool) -> Result<Reply,ClientError> {
        if !valid_component(tool) {return Err(ClientError::invalid());}
        let url=format!("{}/api/workspaces/{}/tools/{tool}/invocations{suffix}",self.base,self.workspace);
        let mut request=self.http.request(if body.is_some(){Method::POST}else{Method::GET},url);
        if let Some(bytes)=body {request=request.header("Content-Type","application/json").body(bytes);}
        let response=request.send().map_err(|_|ClientError::unconfirmed("transport",id))?;
        let status=response.status().as_u16();
        let is_json=response.headers().get("Content-Type").and_then(|v|v.to_str().ok())
            .map(|v|v.split(';').next().unwrap_or("").trim()=="application/json").unwrap_or(false);
        if !(200..600).contains(&status) || (300..400).contains(&status) || !is_json {return Err(ClientError::unconfirmed("protocol",id));}
        let mut bytes=Vec::new();
        response.take(MAX_RESPONSE_BYTES+1).read_to_end(&mut bytes).map_err(|_|ClientError::unconfirmed("transport",id))?;
        if bytes.len() as u64>MAX_RESPONSE_BYTES {return Err(ClientError::unconfirmed("protocol",id));}
        let value:Value=serde_json::from_slice(&bytes).map_err(|_|ClientError::unconfirmed("protocol",id))?;
        validate_response(&value,id,tool,approval)?;
        Ok(Reply{status,body:value})
    }
}
fn validate_response(value: &Value, id: &str, tool: &str, approval: bool) -> Result<(),ClientError> {
    let fail=||ClientError::unconfirmed("protocol",id);
    let data=value.as_object().ok_or_else(fail)?;
    if !data.contains_key("success") && !data.contains_key("invocationId") {
        let code=value["errorCode"].as_str().ok_or_else(fail)?;
        if code.is_empty() || code.len()>96 || !code.bytes().all(|c|c.is_ascii_lowercase()||c.is_ascii_digit()||c==b'-') {return Err(fail());}
        return Ok(());
    }
    if value["invocationId"].as_str()!=Some(id) {return Err(fail());}
    let states=["Pending","Approved","Rejected","Expired","Cancelled","Consumed"];
    if approval {
        if !value["approvalState"].as_str().is_some_and(|s|states.contains(&s)) || !value["expiresAt"].is_string() {return Err(fail());}
    } else {
        if !value["success"].is_boolean() || !value["output"].is_string() || !value["duration"].is_string()
            || value["toolName"].as_str()!=Some(tool) || !value["outcomeRecorded"].is_boolean() || !value["isReplay"].is_boolean() {return Err(fail());}
        if !value["outcome"].is_null() && !value["outcome"].as_str().is_some_and(|s|["Succeeded","Failed","Denied","Cancelled","OutcomeUnknown","NotDispatched"].contains(&s)) {return Err(fail());}
        if value["success"]==true && value["outcome"]!="Succeeded" {return Err(fail());}
        if !value["approvalState"].is_null() && !value["approvalState"].as_str().is_some_and(|s|states.contains(&s)) {return Err(fail());}
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shared_vectors_preserve_unicode_and_null_semantics() {
        let fixtures:Value=serde_json::from_str(include_str!("../../../protocol/governed-tools/requests.json")).unwrap();
        for fixture in fixtures.as_array().unwrap() {
            let request:Invocation=serde_json::from_value(fixture["request"].clone()).unwrap();
            let encoded=serde_json::to_value(request).unwrap();
            for key in ["invocationId","toolName","method","parameters","rawInput"] {
                assert_eq!(encoded[key],fixture["request"][key]);
            }
        }
    }
    #[test]
    fn invalid_ids_and_origins_reject_before_io() {
        for id in ["", "../other", "00000000000000000000000000000000"] {assert!(canonical_id(id).is_err());}
        for url in ["http://remote.example","https://user:pass@example.com","https://example.com/../admin","https://example.com/?q=a"] {assert!(base_url(url).is_err());}
    }
    #[test]
    fn unknown_or_incomplete_results_cannot_be_success() {
        assert!(validate_response(&serde_json::json!({"success":true}),"id","files",false).is_err());
    }
}
