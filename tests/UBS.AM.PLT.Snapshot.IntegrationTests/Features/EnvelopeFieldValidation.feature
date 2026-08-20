Feature: Envelope field validation
    As the Portfolio Snapshot write path
    I need identity fields that are too long or carry unsafe characters to be rejected outright
    So that an over-long or path-unsafe value never reaches blob storage, received_files or a
    SQL write that could only fail as a truncation error

    # Mode A (this project) drives real messages straight through the production
    # ISnapshotMessageHandler with Kafka bypassed. SnapshotEnvelopeValidator checks the four
    # path/identity fields (SnapshotId, AccountId, SnapshotType, PayloadType) for length
    # (max 100, SnapshotFieldLimits) and for blob-path-safe characters before the first write.
    # A violation raises InvalidSnapshotEnvelopeException (FIELD_TOO_LONG / INVALID_FIELD_CHARACTERS),
    # the same non-retryable SnapshotMessageRejectedException category as the existing
    # unexpected-payload-type rejection: the consumer commits past it rather than redelivering
    # forever. No blob and no index row is ever written for the rejected message, but a FAILED
    # tracking row IS recorded (status + reason) whenever SnapshotId itself is storable — it is
    # the one durable trace a rejection leaves, and it is recoverable by a later valid message.

Scenario: An over-long AccountId is rejected before any payload is written, then the snapshot completes normally
    Given a field-validation snapshot with a well-formed account id
    When a payload arrives naming a 101-character account id
    Then the field-validation delivery is rejected for an over-long field
    And the field-validation snapshot has a FAILED tracking row and nothing else stored
    When the field-validation orders payload arrives
    And the field-validation portfolio payload arrives
    And the field-validation settings payload arrives
    And the field-validation header payload arrives
    Then the field-validation snapshot reaches COMPLETE with a completed time
    And a field-validation index row has been written
