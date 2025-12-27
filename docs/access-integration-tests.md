# Access Control Integration Test Design

This document specifies integration tests for the authorization and access-control system. These tests are **not** implemented in this change set.

## Harness Assumptions
- Test server supports a dev auth header: `x-grace-user-id`.
- Orleans + Cosmos emulator are available (current test harness uses these).
- Tests can call HTTP endpoints directly using JSON payloads.
- Each test uses a unique CorrelationId to avoid idempotency collisions.

## Scenario A: Role Inheritance Across Hierarchy

### Setup
1. Create Owner O1, Organizations A and B under O1.
2. Create Repository R1 under Org A and Repository R2 under Org B.
3. Create user U (internal principal id is a string).
4. Grant:
   - OrgAdmin at Org A to U.
   - OrgReader at Org B to U.

### HTTP Calls
- `POST /access/role/grant` for Org A:
  - principal = `{ type: "user", id: "<U>" }`
  - scope = `{ type: "org", ownerId: "<O1>", orgId: "<A>" }`
  - roleId = `OrgAdmin`
- `POST /access/role/grant` for Org B:
  - principal = `{ type: "user", id: "<U>" }`
  - scope = `{ type: "org", ownerId: "<O1>", orgId: "<B>" }`
  - roleId = `OrgReader`

### Assertions
1. `POST /access/check` with resource Repo(O1,A,R1), operation `RepoWrite` → **Allowed**.
2. `POST /access/check` with resource Repo(O1,B,R2), operation `RepoWrite` → **Denied**.
3. `POST /access/check` with resource Repo(O1,B,R2), operation `RepoRead` → **Allowed**.

### Cleanup
- Revoke assignments using `POST /access/role/revoke`.

## Scenario B: Path ACL Deny Overrides Role Allow

### Setup
1. Use data from Scenario A.
2. Ensure U has `RepoContributor` on Repo R1 (via OrgAdmin or direct repo grant).
3. Add Path ACL:
   - Deny U write at `/images` in Repo R1.

### HTTP Calls
- `POST /access/path/add`
  - principal = `{ type: "user", id: "<U>" }`
  - ownerId/orgId/repoId = `<O1>/<A>/<R1>`
  - path = `/images`
  - access = `deny`
  - permissions = `[ "write" ]`

### Assertions
1. `POST /access/check` with resource Path(O1,A,R1,"/images"), operation `PathWrite` → **Denied**.
2. `POST /access/check` with resource Path(O1,A,R1,"/docs"), operation `PathWrite` → **Allowed**.

### Cleanup
- Remove path ACL with `POST /access/path/remove`.

## Scenario C: Protected Endpoint Enforcement

### Setup
1. Use user U.
2. Ensure the two protected endpoints are:
   - Repository write endpoint: `/repository/setDescription`
   - Path write endpoint: `/directoryVersion/create`

### HTTP Calls
1. Call the protected endpoint with `x-grace-user-id: <U>` but without grants.
2. Grant `RepoContributor` to U at repo scope.
3. Call the protected endpoint again with same payload.

### Assertions
1. Without grants → HTTP **403**.
2. After grant → HTTP **200**.

### Cleanup
- Revoke role assignment.

## Scenario D: CLI End-to-End

### Setup
- Configure CLI to point at test server.
- Set `x-grace-user-id` via environment or CLI configuration.

### Steps
1. Run `grace access grant-role --user <U> --org <A> --owner <O1> --role OrgAdmin`.
2. Run `grace access test --user <U> --operation RepoWrite --org <A> --owner <O1> --repo <R1>`.

### Assertions
1. `grant-role` results in a persisted assignment (`/access/role/list` returns it).
2. `test` output matches `/access/check` response (Allowed).

### Cleanup
- `grace access revoke-role ...`.
