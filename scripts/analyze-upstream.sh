#!/bin/bash
# Analyze upstream commits and generate a merge plan
# Usage: ./scripts/analyze-upstream.sh [days]
# Default: 7 days

DAYS=${1:-7}
OUTPUT_FILE="docs/UPSTREAM_MERGE_PLAN.md"

echo "Fetching upstream..."
git fetch upstream

echo "Analyzing commits from the last $DAYS days..."

# Get commit count
COMMIT_COUNT=$(git log upstream/main --oneline --since="$DAYS days ago" | wc -l)

cat > "$OUTPUT_FILE" << EOF
# Upstream Merge Plan

**Generated:** $(date +%Y-%m-%d)
**Upstream Branch:** upstream/main
**Analysis Period:** Last $DAYS days ($COMMIT_COUNT commits)

## Commits to Analyze

EOF

# Categorize commits
echo "## Bug Fixes" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
git log upstream/main --oneline --since="$DAYS days ago" | grep -iE "fix|bug|error" >> "$OUTPUT_FILE" || echo "None found" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"

echo "## New Features" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
git log upstream/main --oneline --since="$DAYS days ago" | grep -iE "add|support|feature|new" >> "$OUTPUT_FILE" || echo "None found" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"

echo "## UI Changes" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
git log upstream/main --oneline --since="$DAYS days ago" | grep -iE "ui|style|css|component" >> "$OUTPUT_FILE" || echo "None found" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"

echo "## Refactoring" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
git log upstream/main --oneline --since="$DAYS days ago" | grep -iE "refactor|rename|update|migrate" >> "$OUTPUT_FILE" || echo "None found" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"

echo "## All Commits (chronological)" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"
git log upstream/main --oneline --since="$DAYS days ago" >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"

echo ""
echo "## Files Changed by Upstream" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo "### Most Changed Files" >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"
git diff main..upstream/main --stat | tail -20 >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"

echo ""
echo "## Potential Conflicts" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo "Files we have modified that upstream also changed:" >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"

# Get files we've changed
OUR_FILES=$(git diff upstream/main..main --name-only)
# Get files upstream changed
UPSTREAM_FILES=$(git diff main..upstream/main --name-only)

# Find intersection
for file in $OUR_FILES; do
    if echo "$UPSTREAM_FILES" | grep -q "^$file$"; then
        echo "CONFLICT: $file" >> "$OUTPUT_FILE"
    fi
done
echo '```' >> "$OUTPUT_FILE"

echo ""
echo "Analysis complete! See $OUTPUT_FILE"
echo "Commit count: $COMMIT_COUNT"
