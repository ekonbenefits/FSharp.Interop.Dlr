---
name: check-review
description: Read, verify, address, reply to and resolve Copilot review threads on a PR of this repo. Use when the user says "check the review" or asks to address PR comments.
---

# Check a Copilot review

Argument: PR number (default: the PR for the current branch, `gh pr view --json number`).
Repo: `ekonbenefits/FSharp.Interop.Dlr`.

## Rules from the owner

- **Never request a Copilot review while waiting for one.** A review takes 15–20 minutes. If
  none has landed yet, schedule a wakeup (~18 minutes) instead of polling in a loop.
- Request a new review only when the user asks:
  `gh api -X POST repos/ekonbenefits/FSharp.Interop.Dlr/pulls/N/requested_reviewers -f 'reviewers[]=copilot-pull-request-reviewer[bot]'`
  (the bot disappears from `requested_reviewers` as soon as it starts; that is not a failure).

## Steps

1. Confirm a review exists for the current head:
   `gh api repos/ekonbenefits/FSharp.Interop.Dlr/pulls/N/reviews --jq '.[] | select(.user.login=="copilot-pull-request-reviewer[bot]") | "\(.submitted_at) \(.commit_id[0:7])"'`
2. List unresolved threads (thread id, path:line, outdated flag, first comment body):
   ```
   gh api graphql -f query='{ repository(owner:"ekonbenefits", name:"FSharp.Interop.Dlr") { pullRequest(number:N) { reviewThreads(first:50) { nodes { id isResolved isOutdated comments(first:1) { nodes { path line body } } } } } } }' --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | "\(.id)\n\(.comments.nodes[0].path):\(.comments.nodes[0].line) outdated=\(.isOutdated)\n\(.comments.nodes[0].body)\n----"'
   ```
3. **Verify every claim before changing code.** Copilot has asserted run-time failures that do not
   happen (e.g. "`unbox<unit> null` throws" — unit *is* null). For a claimed failure, write the
   test first; if it passes, keep the test as a pin and reply explaining why the claim is wrong.
   Its reliable hits are "you handled X on this path but not the parallel one" — check those
   parallels (see the `new-binder` skill's list).
4. Fix the valid ones. Run the gate: `dotnet test -c Debug` and `dotnet test -c Release`
   (Release inlining has broken things Debug passed). If binders or translation changed, also
   build and run `Tests.Wasm` (see CLAUDE.md).
5. Commit with a message starting `Review:`, push to the PR branch.
6. Reply to **every** thread with what was done and the commit SHA (or why no change), then
   resolve it, in one mutation per thread:
   ```
   gh api graphql -f query='mutation($t:ID!,$b:String!){ addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$t, body:$b}){ comment{ id } } resolveReviewThread(input:{threadId:$t}){ thread{ isResolved } } }' -F t=THREAD_ID -F b="Done in SHA."
   ```
7. Report to the user: valid / invalid / doc-only counts, the SHA, test totals. Do not
   re-request a review.
