Feature: Snapshot completion and end-to-end ordering
    As the Portfolio Snapshot write path
    I need a snapshot to complete only once every required file has arrived
    So that the audit index row is written exactly once, independent of arrival order

Scenario: The final required payload completes the set and writes the index via upsert
    Given a completion snapshot for account "IT-ACC-002"
    When the orders payload is received
    Then the snapshot is still receiving with no index row
    And a receiving response was published listing the outstanding files
    When the calculations payload is received
    Then the snapshot is still receiving with no index row
    When the settings payload is received
    Then the snapshot is still receiving with no index row
    When the standard header payload is received
    Then the snapshot tracking is COMPLETE with a completed time
    And the "header.json" blob holds the sent payload
    And the completion index row matches the sent header
    And exactly one completion response was published for the snapshot

Scenario: A full snapshot in canonical order is queryable with correct display data
    Given a completion snapshot for account "IT-ACC-002"
    When the standard header payload is received
    And the orders payload is received
    And the calculations payload is received
    And the settings payload is received
    Then the snapshot end state is a well-formed completed snapshot

Scenario: A full snapshot in shuffled order reaches the same end state as canonical order
    Given a completion snapshot for account "IT-ACC-002"
    When the settings payload is received
    And the calculations payload is received
    And the standard header payload is received
    And the orders payload is received
    Then the snapshot end state is a well-formed completed snapshot
