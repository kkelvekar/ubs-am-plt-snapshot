Feature: Partial snapshot receipt tracking
    As the Portfolio Snapshot write path
    I need each payload of a snapshot to be recorded as it arrives
    So that a snapshot is only published to the audit index once every required file is present

Scenario: The first payload for a brand-new snapshot is recorded as receiving
    Given a new snapshot for account "IT-ACC-001"
    When the orders payload arrives
    Then the "orders.json" blob under the snapshot root contains the sent payload
    And the snapshot tracking status is "RECEIVING"
    And the tracking row lists received files "orders.json"
    And the tracking root path matches the snapshot's ADLS path
    And the tracking first-received and last-updated times both equal the arrival time
    And no index row exists for the snapshot

Scenario: A second non-final payload updates tracking without completing
    Given a new snapshot for account "IT-ACC-001"
    When the orders payload arrives
    And some time passes
    And the portfolio payload arrives
    Then the "portfolio.json" blob under the snapshot root contains the sent payload
    And the tracking row lists received files "orders.json,portfolio.json"
    And the tracking last-updated time equals the most recent arrival
    And the tracking first-received time equals the initial arrival
    And the snapshot tracking status is "RECEIVING"
    And the tracking root path matches the snapshot's ADLS path
    And no index row exists for the snapshot

Scenario: Payloads arriving out of order accumulate and complete only when the header arrives last
    Given a new snapshot for account "IT-ACC-001"
    When the orders payload arrives
    Then the snapshot tracking status is "RECEIVING"
    And no index row exists for the snapshot
    When the settings payload arrives
    Then the snapshot tracking status is "RECEIVING"
    And no index row exists for the snapshot
    When the compliances payload arrives
    Then the snapshot tracking status is "RECEIVING"
    And no index row exists for the snapshot
    When the portfolio payload arrives
    Then the snapshot tracking status is "RECEIVING"
    And no index row exists for the snapshot
    When the orders-history payload arrives
    Then the snapshot tracking status is "RECEIVING"
    And no index row exists for the snapshot
    When the standard header payload arrives
    Then the snapshot tracking status is "COMPLETE"
    And the tracking completed time is set
    And all required blobs exist under the snapshot root
    And an index row is written with the header details
