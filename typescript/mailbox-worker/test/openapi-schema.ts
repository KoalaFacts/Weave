export type Schema = {$ref?: string; type?: string | string[]; oneOf?: Schema[]; anyOf?: Schema[]; const?: unknown; enum?: unknown[]; required?: string[];
    properties?: Record<string, Schema>; additionalProperties?: boolean | Schema; items?: Schema; minLength?: number; maxLength?: number; pattern?: string;
    minimum?: number};
export type Spec = {components: {schemas: Record<string, Schema>}};

/** Checks `value` against the subset of JSON Schema that the published OpenAPI uses; returns the first mismatch, or null. */
export function mismatch(spec: Spec, schema: Schema, value: unknown, at: string): string | null {
    if (schema.$ref) return mismatch(spec, spec.components.schemas[schema.$ref.replace('#/components/schemas/', '')], value, at);
    if (schema.oneOf) return schema.oneOf.filter(option => mismatch(spec, option, value, at) === null).length === 1 ? null : at + ' matches no single oneOf option';
    if (schema.anyOf) return schema.anyOf.some(option => mismatch(spec, option, value, at) === null) ? null : at + ' matches no anyOf option';
    const kind = value === null ? 'null' : Array.isArray(value) ? 'array' : Number.isInteger(value) ? 'integer' : typeof value;
    const types = schema.type === undefined ? [] : [schema.type].flat();
    if (types.length && !types.includes(kind) && !(kind === 'integer' && types.includes('number'))) return `${at} is ${kind}, not ${types.join('|')}`;
    if ('const' in schema && value !== schema.const) return `${at} is not ${JSON.stringify(schema.const)}`;
    if (schema.enum && !schema.enum.includes(value)) return `${at} is not one of ${JSON.stringify(schema.enum)}`;
    if (typeof value === 'string') {
        if (schema.minLength !== undefined && value.length < schema.minLength) return `${at} is shorter than ${schema.minLength}`;
        if (schema.maxLength !== undefined && value.length > schema.maxLength) return `${at} is longer than ${schema.maxLength}`;
        if (schema.pattern !== undefined && !new RegExp(schema.pattern, 'u').test(value)) return `${at} does not match ${schema.pattern}`;
    }
    if (typeof value === 'number' && schema.minimum !== undefined && value < schema.minimum) return `${at} is below ${schema.minimum}`;
    if (Array.isArray(value)) {
        for (const [index, item] of value.entries()) { const found = schema.items ? mismatch(spec, schema.items, item, `${at}[${index}]`) : null; if (found) return found; }
    } else if (kind === 'object') {
        const object = value as Record<string, unknown>;
        for (const name of schema.required ?? []) if (!(name in object)) return `${at}.${name} is required but missing`;
        for (const [name, item] of Object.entries(object)) {
            const own = schema.properties?.[name] ?? (typeof schema.additionalProperties === 'object' ? schema.additionalProperties : undefined);
            if (!own) { if (schema.additionalProperties === false) return `${at}.${name} is not documented`; continue; }
            const found = mismatch(spec, own, item, `${at}.${name}`);
            if (found) return found;
        }
    }
    return null;
}
