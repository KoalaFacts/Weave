import { type Database, type Statement } from './database';
import { fail } from './wire';

// Count statements, including transaction guards, before sending them to D1.
// Exhaustion rejects the operation before committing staged writes.
export function budgetDatabase(db: Database, maximum = 48): Database {
    let used = 0;
    const consume = (n: number) => { if (used+n > maximum) fail(503,'storage'); used += n; };
    class BudgetStatement implements Statement {
        constructor(readonly statement: Statement) {}
        bind(...args: (string|number|null)[]): Statement { return new BudgetStatement(this.statement.bind(...args)); }
        async all<T>(): Promise<{results:T[]}> { consume(1); return this.statement.all<T>(); }
        async first<T>(column?: string): Promise<T|null> { consume(1); return this.statement.first<T>(column); }
        async run(): Promise<unknown> { consume(1); return this.statement.run(); }
    }
    return {
        prepare: sql => new BudgetStatement(db.prepare(sql)),
        async batch(statements) { consume(statements.length); return db.batch(statements.map(s => (s as BudgetStatement).statement)); }
    };
}
