/** Field-level pre-checks for private MCP tool arguments, so a model can correct its own call; the protocol
 * validation still runs afterwards and remains the decision. Reasons never echo the submitted value.
 */
import { invalidField } from './wire';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function checkUuid(value: unknown, field: string): asserts value is string {
    if (typeof value !== 'string' || !uuidPattern.test(value)) invalidField(field, 'must be a lowercase UUID');
}
