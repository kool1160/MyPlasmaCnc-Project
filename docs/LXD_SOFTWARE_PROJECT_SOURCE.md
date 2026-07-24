# Laser X Design (LXD SOFTWARE) — Project Source File

## Project Identity

**Name:** Laser X Design  
**Short name:** LXD SOFTWARE  
**Current authoritative repository:** `kool1160/MyPlasmaCnc-Project`

Laser X Design (LXD SOFTWARE) is a professional industrial CAD/CAM platform for plasma cutting and fabrication.

## Mission

Build a modern, reliable, AI-assisted engineering platform capable of:

- CAD creation and part editing
- Plasma CAM
- Nesting
- Toolpath generation
- Manufacturing validation
- Reverse engineering workflows
- Automated drawing creation
- AI-assisted fixture and part design
- Industrial automation integration

The software must prioritize correctness, reliability, deterministic behavior, maintainability, engineering accuracy, and machine safety over implementation speed.

## Authoritative Source

GitHub is the single source of truth.

Repository content overrides:

- conversation history
- AI memory
- summaries and handoffs
- uploaded PDFs and notes
- previous plans or assumptions

When information conflicts, the current repository state wins, including:

- the current `main` branch
- accepted Pull Requests
- GitHub Issues and Discussions
- `AGENTS.md`
- project documentation
- accepted ADRs
- source code
- tests
- CI results

## Required Repository Review

Before substantial planning, implementation, code review, task generation, architectural recommendations, or completion claims:

1. Inspect the current repository.
2. Read `AGENTS.md`.
3. Read relevant documentation.
4. Review applicable ADRs.
5. Read the complete GitHub Issue.
6. Review linked Pull Requests and comments.
7. Review CI results.
8. Review existing tests.
9. Confirm the current repository state.

Never rely solely on conversation memory.

## Engineering Priorities

Development must prioritize:

- correctness
- deterministic behavior
- machine safety
- maintainability
- modular architecture
- reproducible builds
- automated testing
- clear diagnostics
- documentation
- long-term extensibility

Prefer simple, evidence-based solutions that are easy to test and maintain.

Avoid unnecessary complexity, speculative abstractions, premature optimization, dead code, and unnecessary dependencies.

## Development Rules

Changes should be:

- incremental
- evidence-based
- backward-compatible where practical
- isolated to one bounded problem
- validated with tests
- documented
- delivered through reviewable Pull Requests

Each Pull Request should reference its Issue, include verification evidence, avoid unrelated refactoring, and remain small enough to review confidently.

## AI Role

AI assistants act as engineering contributors, not engineering authorities.

AI may help with:

- repository analysis
- architecture review
- implementation
- testing
- documentation
- diagnostics
- task generation
- automation
- technical-debt identification

AI must not replace deterministic engineering logic for:

- geometry
- toolpaths
- motion control
- machine safety
- manufacturing validation
- fault handling

All safety-critical and manufacturing-critical behavior must remain independently testable.

## Validation Standard

No feature is complete until validated.

Validation should include, where practical:

- unit tests
- integration tests
- regression tests
- deterministic verification
- CI validation
- simulation
- controlled hardware testing
- documented evidence

Experimental machine-control functionality must remain isolated until proven safe.

## Continuous Improvement

Contributors should continually identify evidence-based opportunities to improve:

- architecture
- modularity
- code quality
- test quality
- diagnostics
- performance
- reliability
- developer experience
- documentation
- automation

Every engineering decision should move LXD SOFTWARE toward a professional industrial engineering platform without sacrificing quality or safety.
