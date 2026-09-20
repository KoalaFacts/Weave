use std::{env, io::{self, Read}, process::ExitCode};
use weave_governed_client::{Client, ClientError, Invocation, MAX_REQUEST_BYTES, canonical_id};

fn run() -> Result<(),ClientError> {
    let args:Vec<String>=env::args().skip(1).collect();
    if args.len()!=5 || !["invoke","status","approval"].contains(&args[0].as_str()) {return Err(ClientError::invalid());}
    let capability=env::var("WEAVE_CAPABILITY").map_err(|_|ClientError::invalid())?;
    let bearer=env::var("WEAVE_GLOBAL_BEARER").ok();
    let timeout=env::var("WEAVE_REQUEST_TIMEOUT_MS").unwrap_or_else(|_|"30000".into()).parse::<u64>().map_err(|_|ClientError::invalid())?;
    let client=Client::new(&args[1],&args[2],&capability,bearer.as_deref(),timeout)?;
    let id=canonical_id(&args[4])?;
    let result=match args[0].as_str() {
        "invoke"=>{
            let mut bytes=Vec::new();
            io::stdin().take((MAX_REQUEST_BYTES+1) as u64).read_to_end(&mut bytes).map_err(|_|ClientError::invalid())?;
            if bytes.len()>MAX_REQUEST_BYTES {return Err(ClientError::invalid());}
            let request:Invocation=serde_json::from_slice(&bytes).map_err(|_|ClientError::invalid())?;
            if canonical_id(&request.invocation_id)?!=id || request.tool_name!=args[3] {return Err(ClientError::invalid());}
            client.invoke(&request)?
        },
        "status"=>client.get_invocation(&args[3],&id)?,
        _=>client.get_approval(&args[3],&id)?,
    };
    println!("{}",serde_json::to_string(&result).map_err(|_|ClientError::invalid())?);
    Ok(())
}
fn main() -> ExitCode {
    if env::args().nth(1).as_deref()==Some("--help") {
        println!("weave-client invoke|status|approval URL WORKSPACE TOOL INVOCATION_ID\nInvoke reads the original JSON from stdin. Credentials: WEAVE_CAPABILITY; optional WEAVE_GLOBAL_BEARER.\nNo token minting, automatic IDs, approval decisions or retries. Exit 0 means a validated response, not successful execution; inspect status/body.");
        return ExitCode::SUCCESS;
    }
    match run() {
        Ok(())=>ExitCode::SUCCESS,
        Err(error)=>{eprintln!("{}",serde_json::to_string(&error).unwrap_or_else(|_|"{\"kind\":\"protocol\"}".into()));ExitCode::from(2)},
    }
}
