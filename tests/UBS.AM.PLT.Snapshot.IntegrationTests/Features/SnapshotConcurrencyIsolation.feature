Feature: Snapshot concurrency and partition isolation
    As the Portfolio Snapshot write path
    I need each snapshot to be tracked strictly by its own snapshotId
    So that interleaved or sequential processing of different snapshots never lets one observe or mutate another

Scenario: Interleaved payloads for two snapshots on different partitions never cross-contaminate
    Given concurrent snapshot processing begins
    And snapshot "A" is registered under account "IT-ACC-003"
    And snapshot "B" is registered under account "IT-ACC-004"
    When snapshot "A" gets its orders payload
    And snapshot "B" gets its own distinct orders payload
    Then snapshot "A" is RECEIVING with received files "orders.json"
    And snapshot "B" is RECEIVING with received files "orders.json"
    And snapshot "A" and snapshot "B" have different tracking roots
    And snapshot "A" tracking root contains the segment "accountId=IT-ACC-003/"
    And snapshot "B" tracking root contains the segment "accountId=IT-ACC-004/"
    And snapshot "A" has no index row
    And snapshot "B" has no index row
    When snapshot "A" gets its settings payload
    And snapshot "B" gets its calculations payload
    And snapshot "A" gets its calculations payload
    And snapshot "B" gets its settings payload
    And snapshot "A" gets its standard header payload
    Then snapshot "A" is COMPLETE with a completed time and an index row
    And snapshot "B" is RECEIVING with received files "orders.json,calculations.json,settings.json"
    And snapshot "B" received files exclude "header.json"
    And snapshot "B" has no completed time
    And snapshot "B" has no index row
    When snapshot "B" gets its standard header payload
    Then snapshot "A" is COMPLETE
    And snapshot "B" is COMPLETE
    And snapshot "A" index row has account "IT-ACC-003" and adls path equal to its tracking root
    And snapshot "B" index row has account "IT-ACC-004" and adls path equal to its tracking root
    And snapshot "A" orders blob equals its sent orders payload
    And snapshot "B" orders blob equals its sent orders payload
    And the two orders blobs differ

Scenario: Two sequential snapshots for the same account stay isolated by snapshotId
    Given concurrent snapshot processing begins
    And snapshot "S1" is registered under account "IT-ACC-005"
    When snapshot "S1" gets its standard header payload
    And snapshot "S1" gets its orders payload
    And snapshot "S1" gets its calculations payload
    And snapshot "S1" gets its settings payload
    Then snapshot "S1" is COMPLETE
    And snapshot "S1" tracking and index are recorded as the isolation baseline
    When 20 minutes pass
    And snapshot "S2" is registered under account "IT-ACC-005"
    And snapshot "S2" gets its standard header payload
    And snapshot "S2" gets its orders payload
    And snapshot "S2" gets its calculations payload
    And snapshot "S2" gets its settings payload
    Then snapshot "S2" is COMPLETE
    And snapshot "S1" tracking is unchanged from the isolation baseline
    And snapshot "S1" index is unchanged from the isolation baseline
    And snapshot "S1" and snapshot "S2" tracking roots differ but share the account prefix
    And snapshot "S1" tracking root contains its own snapshotId
    And snapshot "S2" tracking root contains its own snapshotId
    And the index rows of snapshot "S1" and snapshot "S2" have different snapshotIds
