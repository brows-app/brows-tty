# Review artifact

Use this structure unless the user requests another format. Keep the report understandable without the chat history. Replace example placeholders with concrete evidence; omit inapplicable sections.

```markdown
# Code review

Date: YYYY-MM-DD (local timezone)
Scope: requested files, component, PR, or revision range
Baseline: branch/ref and full commit ID, or not applicable
Target: branch/ref and full commit ID, plus relevant local changes
Comparison: merge-base-to-target, explicit endpoints, or full scope

## Summary

Brief assessment and severity counts. State when there are no confirmed findings.

## Findings

### #1 [P2] Actionable defect title

- Status: Open
- Location: `path/to/file.ext:42-48` (target revision)
- Problem and trigger: What happens, under which reachable conditions.
- Impact: Who or what breaks and why it matters.
- Evidence: Reproduction, failing check, or precise execution trace; label inference.
- Suggested resolution: Necessary behavior or invariant and a practical correction.
- Verification: A focused scenario and the expected result after the fix.

## Validation and coverage

- Inspected scope and any exclusions.
- Commands/checks, working directory where necessary, and observed results.
- Checks not run, reasons, and material residual uncertainty.

## Open questions

Unconfirmed concerns and exactly what evidence would resolve them, if any.
```

Keep separate root causes separate; group repeated manifestations of the same root cause and list affected locations. Give enough evidence for someone else to reproduce or trace the defect without making the report a transcript of every tool call. Do not copy secrets or sensitive payloads into the artifact.

When resolving an issue, change its heading to `### #1 [P2] Actionable defect title [Resolved]`, set its status to Resolved, and append the correction and validation evidence. Preserve the original problem and issue number. If disproven rather than fixed, mark it Withdrawn with the reason instead of claiming a fix. If verification is incomplete, leave it Open or In progress and explain the remaining work.

For removed code, cite the baseline path and revision explicitly. For runtime or configuration findings without a useful line anchor, cite the relevant artifact and explain why. Never fabricate line numbers or reproduce stale ones from a prior revision without checking.
