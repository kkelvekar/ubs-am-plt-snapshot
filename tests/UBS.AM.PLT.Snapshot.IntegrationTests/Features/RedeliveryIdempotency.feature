Feature: Redelivery and idempotency
    As the Portfolio Snapshot write path
    I need redelivery of any message - before or after completion - to be harmless
    So that a Kafka redelivery never produces a duplicate row, a mutated row, or a double-counted file

    # Mode A (this project) drives real messages straight through the production
    # ISnapshotMessageHandler with Kafka bypassed. The first scenario proves that redelivering a
    # NON-header payload after the snapshot is already COMPLETE is a no-op (blob overwritten
    # byte-identical, tracking row touched only on last_updated_at, index UPSERT produces no
    # duplicate). The second proves that a same-payloadType duplicate arriving BEFORE completion is
    # de-duplicated in received_files and does not block the remaining files from completing. Full
    # offset-commit-under-redelivery proof is Mode B only (tools/fault-injection.ps1).

Scenario: Redelivering a non-header payload after completion leaves everything but last-updated intact
    Given a redelivery snapshot for account "IT-ACC-007"
    When an orders payload is delivered
    And a calculations payload is delivered
    And a settings payload is delivered
    And the completing header payload is delivered
    Then the snapshot is COMPLETE and captured as the redelivery baseline
    When the redelivery clock advances by 4 minutes
    And the same "orders" payload is redelivered
    Then the redelivery completes without error
    And exactly one tracking row remains, COMPLETE, with only its last-updated time advanced
    And exactly one index row remains, unchanged from the redelivery baseline
    And the redelivered "orders" blob is byte-identical to the originally sent payload

Scenario: A same-payload duplicate before completion is counted once and still completes
    Given a redelivery snapshot for account "IT-ACC-007"
    When an orders payload is delivered
    And the redelivery clock advances by 2 minutes
    And the same orders payload is delivered again
    Then the pre-completion snapshot is RECEIVING with received files "orders.json"
    And no index row exists yet for the snapshot
    When a calculations payload is delivered
    And a settings payload is delivered
    And the completing header payload is delivered
    Then the snapshot is COMPLETE with a completed time
    And "orders.json" appears exactly once among the 4 received files
    And an index row now exists for the snapshot
