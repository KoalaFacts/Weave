// Minimal surface of the Workers runtime module used here; the runtime supplies the implementation.
declare module 'cloudflare:workers' {
    export abstract class WorkerEntrypoint<Environment = unknown> {
        constructor(context: unknown, environment: Environment);
        protected readonly env: Environment;
    }
}
