# Declared plugin service dependencies

The Host's trusted built-in Webhook connector can now consume the HTTP service
provided by a named HTTP plugin. This gives the existing runtime registry one
real provider-to-consumer path. A Webhook definition may declare
`requires: { http: "api" }` with `config.url: "events"`; the HTTP plugin named
`api` supplies the client and base URL. The same shape is accepted by
`POST /api/plugins` as a `requires` object. Without that declaration, a Webhook
continues to use its own client and an absolute URL.

The registry checks that the requested service is supported by the consumer,
that the named provider is active and advertises the service, and that the
consumer can actually obtain the client. It activates providers before
dependents in `ConnectAllAsync`, reports the active dependency in
`GET /api/plugins/composition`, and rejects provider disconnect or replacement
while a dependent is active. Host shutdown disconnects dependents first.
Relative Webhook URLs cannot switch the initial request to another origin.
HTTP base URLs must use HTTP or HTTPS.

This is a process-local lifecycle relationship among Host-owned connectors.
It is not a persistent installation grant, an untrusted-code sandbox, a
credential broker, or a general dependency solver for external plugins.
`plugin:connect`/`plugin:disconnect` remain the management permissions; the
dependency declaration does not grant an Agent any tool operation. A provider
must be explicitly connected again after a Host restart. HTTP redirects and
network egress still follow the Host's HTTP client and deployment policy.
