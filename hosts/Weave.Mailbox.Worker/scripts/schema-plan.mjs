// Schema differences are reviewable output, never permission to discard data.
export function assertAdditiveSchemaPlan(statements) {
    for (const statement of statements) {
        const sql=statement.replace(/\/\*[\s\S]*?\*\//g,'').replace(/--[^\n]*/g,'').trim();
        if (/\bDROP\s+(?:TABLE|COLUMN)\b/i.test(sql) || /^(?:TRUNCATE|DELETE|REPLACE)\b/i.test(sql))
            throw Error('Destructive schema change requires separate review; no statements applied.');
        if (!/^(?:CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX)\b|ALTER\s+TABLE\s+[^;]+\s+ADD\s+COLUMN\b)/i.test(sql) || /;\s*\S/.test(sql))
            throw Error('Unsupported schema change requires review; no statements applied.');
    }
}
