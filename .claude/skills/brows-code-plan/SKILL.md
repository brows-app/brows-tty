---
name: brows-code-plan
description: Plan substantial code changes and save implementation-ready plans under agents/plans. Use for features, refactors, migrations, and other multi-step engineering changes that need a clear design and handoff; not for implementing code or conducting a code review.
argument-hint: "[what to plan]"
---

# Brows code plan

Create a grounded implementation plan that another agent with less capability can follow to produce correct, clear, concise, and well-architected code. Resolve the difficult design decisions during planning rather than leaving them to the implementing agent. Scale detail to the change's complexity and risk.

Request: `$ARGUMENTS`

The plan is a repository file under `agents/plans`, not a Claude Doc, Artifact, or the built-in plan-mode plan. If the session is in plan mode, which blocks file writes, finish the investigation, then use `ExitPlanMode` with a summary saying the plan will be saved to `agents/plans/...` once approved, and write the file after approval.

## Establish the goal and inspect the code

Identify the intended repository, requested behavior, constraints, and acceptance criteria. Read applicable repository instructions (`CLAUDE.md`, `AGENTS.md`, contribution docs) and inspect the current working-tree state. Trace relevant implementations, callers, data models, configuration, and existing tests before proposing changes. Ground the plan in the actual checkout; do not invent files, symbols, APIs, or dependency capabilities. Clearly distinguish existing locations from proposed new files and symbols.

Infer routine choices from repository patterns and the user's requirements. Ask only about missing information that materially changes behavior, architecture, compatibility, or scope (use `AskUserQuestion` for crisp multiple-choice decisions). Continue independent investigation while waiting. Record assumptions explicitly; do not present an unresolved decision as a settled requirement. If a blocking decision remains, mark the plan as a draft and identify exactly what must be decided before implementation.

Planning alone does not authorize implementation. Create the plan artifact without changing production code, tests, dependencies, or repository configuration unless the user also requests those changes. Preserve existing user work.

## Design for clarity and correctness

Prefer the smallest coherent design that satisfies the requirements and fits the repository. Give responsibilities clear boundaries, keep data flow understandable, and reuse established abstractions where they fit. Avoid unnecessary layers, speculative extensibility, unrelated refactors, and new dependencies without a concrete need. Concise code should remain readable; do not trade clarity for compressed syntax.

Trace the proposed behavior from inputs to observable outputs. Specify relevant contracts, invariants, validation, error behavior, and compatibility. Address concurrency, cancellation, resource ownership, security, persistence, and deployment only where the change makes them relevant. Explain consequential design choices and tradeoffs briefly, including why the chosen approach fits the current code.

Resolve cross-component and sequencing questions before handing off the plan. Identify which consumers must change together and which existing behavior must be preserved. For schema, public API, or deployment changes, describe compatibility, migration order, and recovery where needed. Separate required work from optional follow-up ideas so the implementing agent has a bounded assignment.

## Make the implementation executable

Break the work into ordered steps with explicit dependencies. Each step should identify:

- The existing or proposed files, symbols, and contracts to change.
- The concrete behavior or structural change and why it is needed.
- Important edge cases, preserved behavior, and constraints that prevent plausible incorrect implementations.
- The observable completion condition and appropriate verification.

Use small examples, input/output cases, or pseudocode when they remove ambiguity. Do not fill the plan with speculative full implementations or instructions such as "handle errors appropriately" without specifying the required outcome. Provide enough detail that the implementing agent need not rediscover the architecture or choose between materially different behaviors.

Specify meaningful regression scenarios, including expected results for relevant success, boundary, and failure paths. Identify existing tests to extend or proposed test locations based on inspected repository conventions. Record repository-compatible validation commands when established; label commands or checks that remain unverified. Do not claim checks ran or passed unless they did. Include build, integration, formatting, or manual validation only as appropriate to the change.

## Write the plan artifact

Save each new plan under `{repository-root}/agents/plans/YYYY-MM-DD_Plan-{short-description}.md`. Use the current local date at creation time and a short, descriptive, lowercase hyphenated filename suffix, for example `2026-11-12_Plan-import-validation.md`. Create the directory if needed. Never overwrite an existing plan inadvertently; on a filename collision, append a numeric suffix to the description, such as `import-validation-2`. When explicitly asked to update an existing plan, update that file instead.

Do not stage or commit the plan document unless the user explicitly instructs you to do so. This also applies if other code changes are being committed. Do not change `.gitignore` merely to keep the plan uncommitted. Check the plan's actual Git state, including whether it is already tracked or ignored, and preserve unrelated staged work.

Use a structure suited to the task, normally covering:

- **Goal and acceptance criteria:** observable outcomes and scope boundaries.
- **Current state:** relevant architecture, verified code locations, and starting revision/local edits when they affect the plan.
- **Proposed design:** responsibilities, contracts, data flow, and consequential decisions.
- **Implementation steps:** ordered, concrete tasks with dependencies and completion conditions.
- **Validation:** regression scenarios, expected outcomes, and appropriate commands/checks.
- **Risks and open decisions:** material uncertainties, assumptions, migration/recovery needs, and any implementation blockers.

Omit inapplicable sections. Use repository-relative paths so the plan works in another checkout. Include enough context for a fresh agent without access to the conversation, but avoid repeating repository documentation or generic engineering advice.

## Check the handoff

Reread the plan against the inspected code and user requirements. Check that steps agree with the design, cover affected consumers, and can be followed without guessing essential behavior. Verify linked paths, distinguish proposed files from existing ones, and ensure the validation demonstrates the acceptance criteria. Recheck relevant repository state and disclose any changes that invalidate planning assumptions.

In the final response, link the plan document, briefly describe the proposed approach, and flag material unresolved decisions or validation limits. Confirm the plan remains uncommitted. Do not claim the implementation has been completed merely because the plan is complete.
