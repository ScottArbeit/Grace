namespace Grace.Types

open Grace.Types.Automation

module Eventing =
    // Compatibility aliases. New integrations should use Grace.Types.Automation directly.
    type EventType = AutomationEventType
    type EventEnvelope = AutomationEventEnvelope
