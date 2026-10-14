declare module 'spdx-expression-parse' {
  export default function parse(expression:string):unknown;
}
declare module '@onebeyond/spdx-license-satisfies' {
  export function satisfiesAny(expression:string, licenses:string[]):boolean;
}
