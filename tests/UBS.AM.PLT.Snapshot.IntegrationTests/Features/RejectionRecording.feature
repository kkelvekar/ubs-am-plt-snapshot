Feature: Rejection recording and recovery
    As the Portfolio Snapshot write path
    I need a rejected message to leave an accurate, recoverable trace of why it was refused
    So that the audit trail shows a FAILED snapshot instead of silence, and a later valid
    message can still bring the snapshot to COMPLETE

    # Mode A (this project) drives real messages straight through the production
    # ISnapshotMessageHandler with Kafka bypassed. SnapshotMessageHandler.RecordAndPublishRejectionAsync
    # records a rejection as a FAILED snapshot_tracking row (status + reason only) whenever
    # SnapshotId itself is storable (non-null, <= SnapshotFieldLimits.SnapshotIdMaxLength — it is
    # the table's primary key), and always publishes a rejection response via
    # ISnapshotResponsePublisher, whether or not a row could be recorded. No blob and no index row
    # is ever touched by a rejection. A FAILED row is recoverable: a later valid message for the
    # same snapshot flips it back to RECEIVING (Reason and DeclaredFailedAt cleared) via the
    # existing UpsertReceivedAsync path, and the snapshot can then complete normally.

Scenario: A rejection with a storable SnapshotId records a FAILED row and publishes a rejection response
    Given a rejection-recording snapshot for account "IT-ACC-009"
    When an out-of-contract payload is delivered for the rejection-recording snapshot
    Then the rejection-recording delivery is rejected as an unexpected payload type
    And the rejection-recording snapshot has a FAILED tracking row with the reason recorded
    And a rejection response was published for the rejection-recording snapshot

Scenario: A rejection with an unstorable SnapshotId publishes a response but records no tracking row
    Given a rejection-recording snapshot with a 105-character snapshot id
    When a well-formed orders payload is delivered for the rejection-recording snapshot
    Then the rejection-recording delivery is rejected for an over-long field
    And no tracking row exists for the rejection-recording snapshot
    And a rejection response was published for the rejection-recording snapshot

Scenario: A rejected snapshot fully recovers to COMPLETE once all required files arrive
    Given a rejection-recording snapshot for account "IT-ACC-009"
    When an out-of-contract payload is delivered for the rejection-recording snapshot
    Then the rejection-recording snapshot has a FAILED tracking row with the reason recorded
    When a required orders payload is delivered for the rejection-recording snapshot
    And a required calculations payload is delivered for the rejection-recording snapshot
    And a required settings payload is delivered for the rejection-recording snapshot
    And a required header payload is delivered for the rejection-recording snapshot
    Then the rejection-recording snapshot reaches COMPLETE with the reason and declared-failed time cleared
    And a rejection-recording index row has been written matching the sent header
