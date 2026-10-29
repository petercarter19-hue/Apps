# Skill Notes: monthly sales summary

Started 2026-10-12 in "Sales summary for October". Later sessions add a dated section at the bottom.
Status: made sales-summary (tested on 3 cases)

## Goal, and what "right" looks like
One page from the monthly sales sheet: the month's total, the top 5 products, and anything down more than 10% from last month. [User]

## Inputs and environment
- The month's sheet (.xlsx), exported from the shop. The column names change sometimes. [Observed]
- Last month's sheet, for the comparison.

## What works now (the method, in order)
1. Read both sheets; match products by SKU, not by name. [Verified]
2. Total by product; sort; take the top 5.
3. Flag drops over 10%.
4. Write the page: title, total, top 5 table, drops list.

## The user's choices (with scope and their reason)
- Top 5, not top 10: "it has to fit on one page". Scope: always. [User]
- Green headings: "for this one, it's going to the team". Scope: this output, so it stays out of the skill. [User]

## Failure lessons
- Condition: a product was renamed between months. Symptom: it showed as a 100% drop. Fix: match by SKU. Still uncertain: products with no SKU. [Verified]

## Checks actually done
- Totals matched the sheet's own total row. [Verified]

## Untested ideas
- Read the shop's report page directly instead of the export. [Untested]

## Candidate skills
- sales-summary: new, the monthly one-page summary.

## Session log
- 2026-10-12 3:10 PM: first page made; user corrected top 10 to top 5.
- 2026-10-12 3:40 PM: rename problem found and fixed (SKU match).
- 2026-10-12 4:05 PM: "That's right. Make the skill."
