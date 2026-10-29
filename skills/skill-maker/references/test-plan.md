# Test plan

A skill isn't "tested" until a fresh helper, one that never saw the conversation, has used it on new inputs. Test drafts and their output go in scratch, never live.

## 1. Pick the cases (write them down before running)

| Case | What it proves | Example |
|---|---|---|
| **Transfer** | The normal job works with different valid inputs | Another file, a different length, a different project |
| **Edge** | It catches a missing prerequisite, or a failure seen before | A missing input file; the setting that broke last time |
| **Out of scope** | It turns away a nearby job it shouldn't do | Something that sounds similar but belongs to another skill or none |
| **Regression** (updates only) | Something that passed before still passes | The old normal job, run against the updated skill |

If the skill works on fixed real places (your folders, a live site, a drive), make scratch stand-ins and tell the helper which stand-in replaces which real path. Never point a test at the real ones.

Write each case's pass line from the skill's own "done when" checks, so grading isn't a matter of taste. Where taste does matter (a look, a tone), say so, and leave that part to the user.

## 2. Run each case with a fresh helper

Give it only this brief, filled in. Never give it the original conversation, the notes, or the answer you expect.

> You have never seen the work this skill came from. Read `<path to SKILL.md>` and its references, and follow the skill to do this task: <task>. Inputs: <inputs>. Write everything you make under `<scratch folder>`. Don't change anything outside it, don't message anyone, don't start any session, don't install anything, don't spend money. If the skill says to do any of those, write down what you would have done instead. Then report: (1) what you did, step by step; (2) where you made it; (3) every place the skill was unclear, missing something, or wrong; (4) anything you had to guess.

Run independent cases in parallel. Read what each helper actually made, not just its report.

## 3. Check triggering separately

Give one fresh helper 6 to 8 short requests: half should start the skill, and half are near misses. Ask: "For each request, which skill, if any, would you load first? Don't do the tasks." Before the skill is installed, paste its description among the 4 or 5 nearest skills' descriptions, and note that this was a stand-in.

## 4. Record and fix

Write the results in the skill's test log (`TESTS.md`, outside the folder that ships):

```
## <date> <draft | after update: what changed>
| Case | Input | Pass? | Evidence |
Trigger check: <n>/<n> right; misses: <which>
Status: draft | tested | proven
Not tested: <list>
```

Fix only what the evidence shows. Re-run the failed case, plus one that passed if the fix touched shared steps. If the same kind of failure happens twice, stop and tell the user.

## 5. Statuses

- **Draft:** made from an accepted session, not yet tested.
- **Tested:** passed the listed cases. Name them; it's not a guarantee.
- **Proven:** used for real later, with any corrections folded in through an update.
