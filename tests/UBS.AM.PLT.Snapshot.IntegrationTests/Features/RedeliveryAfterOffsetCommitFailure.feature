Feature: Redelivery after a failed offset commit
    As the Portfolio Snapshot write path
    I need redelivery of an already-completed snapshot's completing message to be a harmless no-op
    So that a Kafka offset commit that fails after the index write leaves no duplicate or mutated state

    # Mode A (this project) covers only the deterministic, Kafka-independent slice: redelivery of
    # the completing (header) message for an already-COMPLETE snapshot, simulating design doc §8
    # Scenario 5 where the index write and MarkComplete both succeed but the subsequent offset
    # commit fails, so the consumer redelivers the same message. The remaining fault-injection
    # cases require the live Kafka consume/commit path and are exercised in Mode B.

Scenario: Redelivery of the completing header after a failed offset commit is a harmless no-op
    Given a failure-scenario snapshot for account "IT-ACC-006"
    When the snapshot receives an orders payload
    And the snapshot receives a portfolio payload
    And the snapshot receives a compliances payload
    And the snapshot receives an orders-history payload
    And the snapshot receives a settings payload
    And the completing header message is delivered for the first time
    Then the snapshot is COMPLETE and its tracking, index and header blob are recorded as the redelivery baseline
    When the failure-scenario clock advances by 3 minutes
    And the same completing header message is redelivered
    Then the redelivery raises no error
    And exactly one tracking row exists, still COMPLETE and unchanged from the redelivery baseline
    And exactly one index row exists, unchanged from the redelivery baseline
    And the header blob still holds the originally sent header content
