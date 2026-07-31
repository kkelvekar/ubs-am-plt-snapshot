Feature: Unexpected payload handling
    As the Portfolio Snapshot write path
    I need a payload that is not in the configured required-files set to be rejected outright
    So that an out-of-contract file never reaches blob storage or received_files

    # Mode A (this project) drives real messages straight through the production
    # ISnapshotMessageHandler with Kafka bypassed. The expected payloads for a snapshotType are
    # exactly its required files, so a payloadType outside that set (auditlog.json) is refused before
    # the first write: no blob, no entry in received_files. The rejection is a
    # SnapshotMessageRejectedException, which the consumer commits past rather than redelivering
    # forever — only the publishing application can fix it by sending a corrected message. A FAILED
    # tracking row IS recorded for the rejection (status + reason only), since SnapshotId itself is
    # storable here; that row is recoverable by a later valid message (FAILED -> RECEIVING -> COMPLETE).
    #
    # The missing/null required envelope field case is covered by unit tests against fakes in the
    # Application and Infrastructure layers, not as a Mode A integration scenario. The
    # unconfigured-snapshotType and header-missing-required-index-fields cases remain open design
    # questions and keep their existing xUnit coverage in MalformedInputTests until resolved.

Scenario: An unexpected file arriving before the required files is rejected and recovers to COMPLETE
    Given an out-of-contract snapshot for account "IT-ACC-008"
    When an unexpected auditlog payload is delivered
    Then the delivery is rejected as an unexpected payload type
    And a FAILED tracking row is recorded and nothing else has been stored for the snapshot
    When a required orders payload is stored
    And a required calculations payload is stored
    And a required settings payload is stored
    And the completing header is stored
    Then the snapshot reaches COMPLETE with a completed time
    And the completed snapshot lists exactly 4 received files excluding "auditlog.json"
    And the "auditlog.json" blob is absent under the snapshot root
    And an index row has been written for the snapshot

Scenario: An unexpected file arriving after completion is rejected and leaves the snapshot untouched
    Given an out-of-contract snapshot for account "IT-ACC-008"
    When a required orders payload is stored
    And a required calculations payload is stored
    And a required settings payload is stored
    And the completing header is stored
    Then the snapshot reaches COMPLETE and is captured as the extra-file baseline
    When the malformed-input clock moves forward by 5 minutes
    And an unexpected auditlog payload is delivered
    Then the delivery is rejected as an unexpected payload type
    And the snapshot is still COMPLETE with the same completed time as the baseline
    And the completed snapshot lists exactly 4 received files excluding "auditlog.json"
    And the "auditlog.json" blob is absent under the snapshot root
    And exactly one index row exists, identical to the extra-file baseline
