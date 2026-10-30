import { createHash, timingSafeEqual } from 'node:crypto';

// Immutable scheme definition: SHA-256, one round, UTF-8 input, no application
// trim/normalization, no salt, 32-byte digest encoded as lowercase hexadecimal.
export const CONTROL_HASH_SCHEME = 'sha256-utf8-v1';
export const CONTROL_HASH_PARAMETERS = Object.freeze({algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1});
export function validParameters(value: unknown): boolean {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
    const entries=Object.entries(CONTROL_HASH_PARAMETERS);
    return Object.keys(value).length === entries.length && entries.every(([k,v]) => (value as Record<string,unknown>)[k] === v);
}
export async function controlDigest(control: string, scheme = CONTROL_HASH_SCHEME): Promise<string | null> {
    switch (scheme) {
        case CONTROL_HASH_SCHEME: return createHash('sha256').update(control,'utf8').digest('hex');
        default: return null;
    }
}
export function digestMatches(candidate: string, stored: string): boolean {
    if (typeof candidate !== 'string' || typeof stored !== 'string' || !/^[0-9a-f]{64}$/.test(candidate) || !/^[0-9a-f]{64}$/.test(stored)) return false;
    const bytes = (hex: string) => Uint8Array.from(hex.match(/../g)!,h => parseInt(h,16));
    // Native workerd comparison. Database index lookup and the full HTTP request
    // are not claimed to have constant timing.
    return timingSafeEqual(bytes(candidate),bytes(stored));
}
