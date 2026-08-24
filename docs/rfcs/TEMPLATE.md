# RFC NNNN: <the question, phrased as a question>

## Status

Open / Decided (ADR NNNN) / Withdrawn. Opened <date>.

An RFC presents options and a recommendation so a decision can be made; it
is not itself the decision. When it is decided, write the [ADR](../adrs/)
and name it here.

## Question

One sentence, phrased so an option can answer it. "Which game events does
the ABI expose first", not "we should expose entity kills".

**Why now.** What forces the choice: a blocked change, a cost, a failure, a
dependency going away.

**Drivers.** The constraints any acceptable option must satisfy: untrusted
guests by default, hard limits, no game object exposure, net48 in-game
bridge, net8 tooling, no em dashes, no AI attribution. Keep them concrete
enough to disqualify something.

**Out of scope.** What this deliberately does not decide.

## Current state

How it works today, including the workaround standing in for a decision.
Name the files, exports, and tests that would change.

## Options

One subsection per option, and the status quo is one of them. Include at
least one option that adds nothing new.

### Option A: <name>

- **What it is:**
- **How it would fit:** files, exports, gates, docs that change.
- **Pros / Cons:**
- **Cost to adopt / cost to back out:**
- **Evidence:** each link with what it actually shows; mark anything
  unverified as unverified.

### Option B: <name>

(same shape)
