// Node 24 implements the JSON source-text APIs; TypeScript's lib declarations
// do not yet describe them. These declarations add no runtime implementation.
interface JSON {
    rawJSON(text:string):{readonly rawJSON:string};
    parse(text:string,reviver:(key:string,value:unknown,context:{source?:string})=>unknown):unknown;
}
