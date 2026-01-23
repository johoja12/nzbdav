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

# Get our fork's commits
FORK_COMMITS=$(git log upstream/main..main --oneline)
FORK_COMMIT_COUNT=$(echo "$FORK_COMMITS" | wc -l)

cat > "$OUTPUT_FILE" << EOF
# Upstream Merge Plan

**Generated:** $(date +%Y-%m-%d)
**Upstream Branch:** upstream/main
**Analysis Period:** Last $DAYS days ($COMMIT_COUNT commits)
**Fork Commits:** $FORK_COMMIT_COUNT commits ahead of upstream

## Implementation Status

This section tracks which upstream patches have been implemented in our fork.

### Status Legend
- ✅ **Implemented**: Patch has been integrated into our fork
- ⏳ **Pending**: Patch is queued for integration
- ⚠️ **Partial**: Partial implementation or different approach
- ❌ **Skipped**: Will not integrate (conflicts or not needed)

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
echo "## Implemented Patches" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo "Upstream patches that have been integrated into our fork:" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo "| Upstream | Fork | Description |" >> "$OUTPUT_FILE"
echo "|----------|------|-------------|" >> "$OUTPUT_FILE"

# Check for matching commit messages between upstream and fork
git log upstream/main --oneline --since="$DAYS days ago" | while read -r line; do
    HASH=$(echo "$line" | cut -d' ' -f1)
    MSG=$(echo "$line" | cut -d' ' -f2-)
    # Look for similar message in our fork (first 30 chars to handle minor differences)
    MSG_SHORT=$(echo "$MSG" | cut -c1-30)
    FORK_MATCH=$(git log upstream/main..main --oneline | grep -F "$MSG_SHORT" | head -1)
    if [ -n "$FORK_MATCH" ]; then
        FORK_HASH=$(echo "$FORK_MATCH" | cut -d' ' -f1)
        echo "| \`$HASH\` | \`$FORK_HASH\` | $MSG |" >> "$OUTPUT_FILE"
    fi
done

echo "" >> "$OUTPUT_FILE"
echo "## Fork-Only Features" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo "Custom features in our fork that don't exist in upstream:" >> "$OUTPUT_FILE"
echo "" >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"
git log upstream/main..main --oneline | head -30 >> "$OUTPUT_FILE"
echo '```' >> "$OUTPUT_FILE"

echo ""
echo "Analysis complete! See $OUTPUT_FILE"
echo "Commit count: $COMMIT_COUNT"
echo "Fork commits: $FORK_COMMIT_COUNT"
