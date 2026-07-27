Feature: Unexpected payload handling
    As the Portfolio Snapshot write path
    I need a payload that is not in the configured required-files set to be stored opaquely
    So that an unexpected extra file never blocks or re-triggers snapshot completion

    # Mode A (this project) drives real messages straight through the production
    # ISnapshotMessageHandler with Kafka bypassed. Both scenarios cover a payloadType
    # (auditlog.json) that is not part of the required-files contract for the snapshotType. Per the
    # opacity invariant the blob is still written and the filename recorded in received_files, but
    # the completeness check (required subset of received) ignores it: the four required files
    # alone drive the snapshot COMPLETE, and an extra file arriving after completion does not re-run
    # the index UPSERT.
    #
    # The missing/null required envelope field case is covered by unit tests against fakes in the
    # Application and Infrastructure layers, not as a Mode A integration scenario. The
    # unconfigured-snapshotType and header-missing-required-index-fields cases remain open design
    # questions and keep their existing xUnit coverage in MalformedInputTests until resolved.

Scenario: An unexpected file arriving before the required files is stored and does not block completion
    Given an out-of-contract snapshot for account "IT-ACC-008"
    When an unexpected auditlog payload is stored
    Then the snapshot remains RECEIVING and its received files include "auditlog.json"
    And no index row has been written for the snapshot
    When a required orders payload is stored
    And a required calculations payload is stored
    And a required settings payload is stored
    And the completing header is stored
    Then the snapshot reaches COMPLETE with a completed time
    And the completed snapshot lists exactly 5 received files including "auditlog.json"
    And the "auditlog.json" blob is present under the snapshot root
    And an index row has been written for the snapshot

Scenario: An unexpected file arriving after completion is stored without re-completing the snapshot
    Given an out-of-contract snapshot for account "IT-ACC-008"
    When a required orders payload is stored
    And a required calculations payload is stored
    And a required settings payload is stored
    And the completing header is stored
    Then the snapshot reaches COMPLETE and is captured as the extra-file baseline
    When the malformed-input clock moves forward by 5 minutes
    And an unexpected auditlog payload is stored
    Then the snapshot is still COMPLETE with the same completed time as the baseline
    And the completed snapshot lists exactly 5 received files including "auditlog.json"
    And the "auditlog.json" blob is present under the snapshot root
    And exactly one index row exists, identical to the extra-file baseline
