# AI Orchestration Design

## Purpose

The AI Orchestrator transforms user intent and incoming communication into structured suggestions, summaries, drafts, tasks, and approval requests.

## Core pattern

```text
Input event or user request
        ↓
Normalize context
        ↓
Retrieve relevant knowledge
        ↓
Call AI model
        ↓
Validate structured output
        ↓
Create draft/task/approval/action
        ↓
Notify affected users
```

## Context sources

- Current user profile and permissions.
- Conversation history.
- Customer and contact records.
- Project records.
- Recent messages and tasks.
- Calendar availability.
- Approved organizational knowledge.
- User writing preferences.
- Connector capabilities.

## Memory layers

- **Organization memory** - standard processes, product information, company knowledge.
- **Team memory** - team-specific procedures and responsibilities.
- **Project memory** - project decisions, files, history, stakeholders.
- **Customer memory** - contacts, preferences, history, open issues.
- **User memory** - private preferences and writing style.
- **Session memory** - temporary conversation state.

## Tool/capability model

AI should propose structured operations such as:

- DraftMessage
- CreateTask
- CreateReminder
- SummarizeConversation
- SearchKnowledge
- RequestApproval
- RecommendRoute

The application decides whether the operation is allowed and which connector can perform it.

## Approval-aware actions

The orchestrator may create drafts and approval requests, but it cannot send messages directly. Sending is performed only by workflow/action execution after approval and authorization checks.

## Prompting standards

System prompts should include:

- Current user's role.
- Current organization's policy.
- Available tools/capabilities.
- Approval requirements.
- Data handling rules.
- Instruction to avoid unsupported claims.
- Instruction to ask for missing critical information when required.

## Evaluation

AI behavior should be tested against scenarios:

- Customer asks for urgent support.
- User asks to send a sensitive reply.
- User asks to see a restricted project.
- Two users attempt to reply to same SMS thread.
- AI lacks enough context.
- AI recommends routing to unavailable employee.
