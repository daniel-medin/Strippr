(() => {
  const form = document.querySelector("[data-processing-form]");
  if (!form) {
    return;
  }

  const fileInput = form.querySelector("input[type='file']");
  const apiKeyInput = form.querySelector("[data-ai-api-key]");
  const languageSelect = form.querySelector("[data-ai-language]");
  const analyzeButton = form.querySelector("[data-ai-content-analyze]");
  const analyzeLabel = form.querySelector("[data-ai-content-analyze-label]");
  const addButton = form.querySelector("[data-ai-content-add]");
  const deleteSelectedButton = form.querySelector("[data-ai-content-delete-selected]");
  const spinner = form.querySelector("[data-ai-content-spinner]");
  const clearButton = form.querySelector("[data-ai-content-clear]");
  const status = form.querySelector("[data-ai-content-status]");
  const feedbackStatus = form.querySelector("[data-ai-content-feedback-status]");
  const modelSelect = form.querySelector("[data-ai-content-model]");
  const thresholdSelect = form.querySelector("[data-ai-content-threshold]");
  const memoryModeSelect = form.querySelector("[data-ai-content-memory-mode]");
  const thresholdSummary = form.querySelector("[data-ai-content-threshold-summary]");
  const enabledToggle = form.querySelector("[data-ai-content-enabled]");
  const results = form.querySelector("[data-ai-content-results]");
  const issuesContainer = form.querySelector("[data-ai-content-issues]");
  const transcriptBody = form.querySelector("[data-ai-content-transcript-body]");
  const transcriptModelTag = form.querySelector("[data-ai-content-transcript-model]");
  const overlay = form.querySelector("[data-ai-content-waveform-overlay]");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const forgetMemoryButton = form.querySelector("[data-ai-content-forget-memory]");
  const panel = form.querySelector(".ai-content-panel");

  if (
    !(fileInput instanceof HTMLInputElement) ||
    !(apiKeyInput instanceof HTMLInputElement) ||
    !(languageSelect instanceof HTMLSelectElement) ||
    !(analyzeButton instanceof HTMLButtonElement) ||
    !(analyzeLabel instanceof HTMLElement) ||
    !(addButton instanceof HTMLButtonElement) ||
    !(deleteSelectedButton instanceof HTMLButtonElement) ||
    !(spinner instanceof HTMLElement) ||
    !(clearButton instanceof HTMLButtonElement) ||
    !(status instanceof HTMLElement) ||
    !(feedbackStatus instanceof HTMLElement) ||
    !(modelSelect instanceof HTMLSelectElement) ||
    !(thresholdSelect instanceof HTMLSelectElement) ||
    !(memoryModeSelect instanceof HTMLSelectElement) ||
    !(thresholdSummary instanceof HTMLElement) ||
    !(enabledToggle instanceof HTMLInputElement) ||
    !(results instanceof HTMLElement) ||
    !(issuesContainer instanceof HTMLElement) ||
    !(transcriptBody instanceof HTMLElement) ||
    !(transcriptModelTag instanceof HTMLElement) ||
    !(overlay instanceof HTMLElement) ||
    !(waveformPanel instanceof HTMLElement) ||
    !(waveformStage instanceof HTMLElement) ||
    !(forgetMemoryButton instanceof HTMLButtonElement) ||
    !(panel instanceof HTMLElement)
  ) {
    return;
  }

  const minimumRangeSeconds = 0.05;
  const modelStorageKey = "strippr.ai.content.model.v2";
  const thresholdStorageKey = "strippr.ai.content.threshold.v1";
  const memoryModeStorageKey = "strippr.ai.content.memory-mode.v1";
  const labelOptions = [
    "False start",
    "Restart",
    "Wrong line",
    "Garble",
    "Abandoned read",
    "Long pause",
    "Correction",
    "Retake",
  ];
  const hasDefaultServerKey = panel.dataset.aiContentDefaultKeyAvailable === "true";
  let isAnalyzing = false;
  let waveformDurationSeconds = 0;
  let isWaveformReady = false;
  let transcriptSegments = [];
  let transcriptText = "";
  let transcriptionModel = "";
  let analysisModel = "";
  let analysisSessionId = "";
  let issueItems = [];
  let lastPublishedRangesKey = "";
  let dragState = null;
  let addModeEnabled = false;
  let selectedRangeRef = null;

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  const getStageWidth = () => waveformStage.clientWidth || Number.parseFloat(waveformStage.style.width) || 0;
  const secondsToX = (seconds) => waveformDurationSeconds > 0
    ? clamp(seconds / waveformDurationSeconds, 0, 1) * getStageWidth()
    : 0;
  const xToSeconds = (clientX) => {
    const width = getStageWidth();
    const bounds = waveformStage.getBoundingClientRect();
    const relativeX = clientX - bounds.left;
    return width > 0 && waveformDurationSeconds > 0
      ? clamp(relativeX / width, 0, 1) * waveformDurationSeconds
      : 0;
  };

  const escapeHtml = (value) => `${value ?? ""}`
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");

  const formatDuration = (seconds) => {
    const safeSeconds = Math.max(0, seconds);
    const wholeSeconds = Math.floor(safeSeconds);
    const minutes = Math.floor(wholeSeconds / 60).toString().padStart(2, "0");
    const remainingSeconds = (wholeSeconds % 60).toString().padStart(2, "0");
    const hundredths = Math.floor((safeSeconds - wholeSeconds) * 100).toString().padStart(2, "0");
    return `${minutes}:${remainingSeconds}.${hundredths}`;
  };

  const createSessionId = () => window.crypto?.randomUUID?.() ?? `a2-${Date.now()}-${Math.round(Math.random() * 100000)}`;
  const createIssueId = () => window.crypto?.randomUUID?.() ?? `issue-${Date.now()}-${Math.round(Math.random() * 100000)}`;
  const getConfidenceThreshold = () => clamp(Number(thresholdSelect.value) || 0, 0, 1);

  const normalizeRanges = (ranges) =>
    Array.isArray(ranges)
      ? ranges
          .map((range) => ({
            startSeconds: Number(range.startSeconds),
            endSeconds: Number(range.endSeconds),
          }))
          .filter((range) =>
            Number.isFinite(range.startSeconds) &&
            Number.isFinite(range.endSeconds) &&
            range.endSeconds > range.startSeconds)
          .sort((left, right) => left.startSeconds - right.startSeconds || left.endSeconds - right.endSeconds)
      : [];

  const cloneRanges = (ranges) => normalizeRanges(ranges).map((range) => ({ ...range }));
  const rangesEqual = (left, right) => {
    const normalizedLeft = normalizeRanges(left);
    const normalizedRight = normalizeRanges(right);
    return normalizedLeft.length === normalizedRight.length &&
      normalizedLeft.every((range, index) =>
        Math.abs(range.startSeconds - normalizedRight[index].startSeconds) < 0.001 &&
        Math.abs(range.endSeconds - normalizedRight[index].endSeconds) < 0.001);
  };

  const isSameSelection = (issueId, rangeIndex) =>
    selectedRangeRef !== null &&
    selectedRangeRef.issueId === issueId &&
    selectedRangeRef.rangeIndex === rangeIndex;

  const getIssueById = (issueId) => issueItems.find((issue) => issue.id === issueId) ?? null;
  const issuePassesConfidenceThreshold = (issue) =>
    Boolean(issue?.isUserAdded) ||
    issue?.reviewStatus !== "pending" ||
    issue?.confidence >= getConfidenceThreshold();
  const getVisibleIssues = () => issueItems.filter((issue) => issuePassesConfidenceThreshold(issue));

  const syncSelectedRange = () => {
    if (!selectedRangeRef) {
      return;
    }

    const selectedIssue = getIssueById(selectedRangeRef.issueId);
    if (!selectedIssue ||
        selectedIssue.reviewStatus === "rejected" ||
        !selectedIssue.ranges[selectedRangeRef.rangeIndex] ||
        !issuePassesConfidenceThreshold(selectedIssue)) {
      selectedRangeRef = null;
    }
  };

  const setSelectedRange = (issueId, rangeIndex = 0) => {
    const issue = getIssueById(issueId);
    if (!issue || issue.reviewStatus === "rejected" || !issue.ranges[rangeIndex]) {
      selectedRangeRef = null;
      return false;
    }

    selectedRangeRef = {
      issueId,
      rangeIndex,
    };

    return true;
  };

  const createUserAddedIssue = (startSeconds, endSeconds) => {
    const range = {
      startSeconds,
      endSeconds,
    };

    return {
      id: createIssueId(),
      originalStartSeconds: startSeconds,
      originalEndSeconds: endSeconds,
      originalRanges: cloneRanges([range]),
      originalLabel: "Missed cut",
      label: "Missed cut",
      correctedLabel: null,
      reason: "Added manually because A2 missed this cut.",
      excerpt: buildTranscriptExcerpt(startSeconds, endSeconds),
      confidence: 1,
      ranges: cloneRanges([range]),
      reviewStatus: "pending",
      editMode: null,
      splitPointSeconds: (startSeconds + endSeconds) / 2,
      hasPendingFeedback: true,
      isUserAdded: true,
      isLearned: false,
    };
  };

  const getActiveRanges = () => normalizeRanges(
    getVisibleIssues().flatMap((issue) => issue.reviewStatus === "rejected" ? [] : issue.ranges));

  const getRenderableRanges = () => getVisibleIssues().flatMap((issue) => {
    if (issue.reviewStatus === "rejected") {
      return [];
    }

    return issue.ranges.map((range, rangeIndex) => ({
      issueId: issue.id,
      rangeIndex,
      range,
      label: getIssueDisplayLabel(issue),
      hasPendingFeedback: issue.hasPendingFeedback,
      isUserAdded: Boolean(issue.isUserAdded),
      isLearned: Boolean(issue.isLearned),
    }));
  });

  const formatRangeSummary = (issue) => issue.ranges
    .map((range) => `${formatDuration(range.startSeconds)} - ${formatDuration(range.endSeconds)}`)
    .join(" | ");
  const formatConfidence = (confidence) => `${Math.round(clamp(confidence, 0, 1) * 100)}%`;
  const formatThreshold = () => `${Math.round(getConfidenceThreshold() * 100)}%+`;
  const useLearnedMemory = () => memoryModeSelect.value !== "fresh";
  const canForgetClipMemory = () =>
    enabledToggle.checked &&
    !isAnalyzing &&
    Boolean(fileInput.files?.[0]) &&
    transcriptText.trim().length > 0;
  const getIssueDisplayLabel = (issue) => `${issue.correctedLabel || issue.label || "Retake"}`.trim();
  const renderLabelOptions = (selectedLabel) => labelOptions
    .map((option) => `<option value="${escapeHtml(option)}"${option === selectedLabel ? " selected" : ""}>${escapeHtml(option)}</option>`)
    .join("");

  const setStatus = (message, { isError = false, hidden = false } = {}) => {
    status.hidden = hidden || !message;
    status.textContent = hidden ? "" : message;
    status.classList.toggle("is-error", isError && !hidden);
  };

  const setFeedbackStatus = (message, { isError = false, hidden = false } = {}) => {
    feedbackStatus.hidden = hidden || !message;
    feedbackStatus.textContent = hidden ? "" : message;
    feedbackStatus.classList.toggle("is-error", isError && !hidden);
  };

  const buildTranscriptExcerpt = (startSeconds, endSeconds) => {
    const excerpt = transcriptSegments
      .filter((segment) => segment.endSeconds > startSeconds && segment.startSeconds < endSeconds)
      .map((segment) => segment.text)
      .join(" ")
      .trim();

    if (excerpt.length > 0) {
      return excerpt;
    }

    return transcriptText.trim().slice(0, 240);
  };

  const updateThresholdSummary = () => {
    const totalIssues = issueItems.length;
    if (!enabledToggle.checked || totalIssues === 0) {
      thresholdSummary.hidden = true;
      thresholdSummary.textContent = "";
      return;
    }

    const visibleCount = getVisibleIssues().length;
    const hiddenCount = Math.max(0, totalIssues - visibleCount);
    thresholdSummary.hidden = false;

    if (getConfidenceThreshold() <= 0) {
      thresholdSummary.textContent = `Showing all ${visibleCount} A2 items.`;
      return;
    }

    if (hiddenCount > 0) {
      thresholdSummary.textContent = `Showing ${visibleCount} of ${totalIssues} A2 items at ${formatThreshold()}. Reviewed and manual cuts stay visible.`;
      return;
    }

    thresholdSummary.textContent = `Showing all ${visibleCount} A2 items at ${formatThreshold()}.`;
  };

  const syncActionButtons = () => {
    syncSelectedRange();

    const canAdd = enabledToggle.checked &&
      Boolean(analysisSessionId) &&
      isWaveformReady &&
      waveformDurationSeconds > 0;

    if (!canAdd) {
      addModeEnabled = false;
    }

    addButton.disabled = !canAdd;
    addButton.classList.toggle("is-active", canAdd && addModeEnabled);
    deleteSelectedButton.disabled = !enabledToggle.checked || selectedRangeRef === null;
    forgetMemoryButton.disabled = !canForgetClipMemory();
    overlay.classList.toggle("is-add-mode", canAdd && addModeEnabled);
  };

  const syncToggleLabel = () => {
    const label = enabledToggle.parentElement?.querySelector(".field-toggle__label");
    if (label instanceof HTMLElement) {
      label.textContent = enabledToggle.checked ? "On" : "Off";
    }
  };

  const updateAnalyzeButtonState = () => {
    analyzeButton.disabled =
      !enabledToggle.checked ||
      isAnalyzing ||
      !fileInput.files?.[0] ||
      (!apiKeyInput.value.trim() && !hasDefaultServerKey);
  };

  const setAnalyzingState = (value) => {
    isAnalyzing = value;
    spinner.hidden = !value;
    analyzeLabel.textContent = value ? "Analyzing..." : "Find retakes";
    updateAnalyzeButtonState();
    syncActionButtons();
  };

  const syncRanges = ({ force = false } = {}) => {
    const issueRanges = getActiveRanges();
    const nextKey = JSON.stringify(issueRanges.map((range) => ({
      startSeconds: Number(range.startSeconds.toFixed(3)),
      endSeconds: Number(range.endSeconds.toFixed(3)),
    })));

    if (!force && nextKey === lastPublishedRangesKey) {
      return;
    }

    lastPublishedRangesKey = nextKey;
    form.dispatchEvent(new CustomEvent("strippr:ai-content-ranges-changed", {
      detail: {
        ranges: issueRanges.map((range) => ({
          startSeconds: range.startSeconds,
          endSeconds: range.endSeconds,
        })),
      },
    }));
  };

  const applyOverlayRegionLayout = (region, range) => {
    const startX = secondsToX(range.startSeconds);
    const endX = secondsToX(range.endSeconds);
    region.style.left = `${startX}px`;
    region.style.width = `${Math.max(8, endX - startX)}px`;
    region.title = `A2 ${formatDuration(range.startSeconds)}-${formatDuration(range.endSeconds)}`;
  };

  const renderOverlay = () => {
    syncSelectedRange();
    const renderableRanges = getRenderableRanges();
    if (!enabledToggle.checked || !isWaveformReady || waveformPanel.hidden || renderableRanges.length === 0) {
      overlay.innerHTML = "";
      clearButton.hidden = !enabledToggle.checked || issueItems.length === 0;
      syncActionButtons();
      return;
    }

    if (getStageWidth() <= 0 || waveformDurationSeconds <= 0) {
      overlay.innerHTML = "";
      clearButton.hidden = issueItems.length === 0;
      syncActionButtons();
      return;
    }

    overlay.innerHTML = "";
    renderableRanges.forEach((entry) => {
      const region = document.createElement("div");
      region.className = "waveform-ai-content-region";
      region.setAttribute("data-ai-content-range", "");
      region.setAttribute("data-issue-id", entry.issueId);
      region.setAttribute("data-range-index", `${entry.rangeIndex}`);
      if (entry.hasPendingFeedback) {
        region.classList.add("is-pending-feedback");
      }
      if (isSameSelection(entry.issueId, entry.rangeIndex)) {
        region.classList.add("is-selected");
      }

      const badge = document.createElement("span");
      badge.className = "waveform-ai-content-badge";
      badge.textContent = entry.isUserAdded ? "ADD" : entry.isLearned ? "MEM" : "A2";
      region.appendChild(badge);

      ["start", "end"].forEach((edge) => {
        const handle = document.createElement("button");
        handle.type = "button";
        handle.className = `waveform-ai-content-handle is-${edge}`;
        handle.setAttribute("data-ai-content-handle", edge);
        handle.setAttribute("data-issue-id", entry.issueId);
        handle.setAttribute("data-range-index", `${entry.rangeIndex}`);
        handle.setAttribute("aria-label", `Adjust A2 ${edge} for ${entry.label}`);
        region.appendChild(handle);
      });

      applyOverlayRegionLayout(region, entry.range);
      overlay.appendChild(region);
    });

    clearButton.hidden = false;
    syncActionButtons();
  };

  const reviewStateLabel = (issue) => {
    switch (issue.reviewStatus) {
      case "accepted": return "Kept";
      case "rejected": return "Deleted";
      case "split": return "Split";
      case "wrong_reason": return "Wrong reason";
      case "adjusted": return "Adjusted";
      default: return "Pending";
    }
  };

  const renderResults = () => {
    const hasTranscript = transcriptSegments.length > 0 || transcriptText.trim().length > 0;
    const visibleIssues = getVisibleIssues();
    const hasIssues = visibleIssues.length > 0;
    const hasAnyIssues = issueItems.length > 0;

    results.hidden = !hasTranscript && !hasIssues;
    issuesContainer.innerHTML = "";
    transcriptBody.innerHTML = "";
    transcriptModelTag.hidden = true;

    if (!results.hidden) {
      if (hasIssues) {
        issuesContainer.innerHTML = visibleIssues.map((issue) => {
          const canSplit = issue.ranges.length === 1;
          const canReset = !rangesEqual(issue.ranges, issue.originalRanges) || issue.reviewStatus !== "pending";
          const isSelected = selectedRangeRef !== null && selectedRangeRef.issueId === issue.id;
          const splitEditor = issue.editMode === "split"
            ? `
              <div class="ai-content-review-editor">
                <div class="ai-content-review-inputs ai-content-review-inputs--single">
                  <label class="ai-content-review-field">
                    <span>Split at</span>
                    <input type="number" min="0" step="0.01" value="${escapeHtml(Number(issue.splitPointSeconds).toFixed(2))}" data-issue-input="split" data-issue-id="${issue.id}" />
                  </label>
                </div>
                <div class="ai-content-review-editor-actions">
                  <button class="secondary-button ai-content-review-button" type="button" data-issue-action="apply-split" data-issue-id="${issue.id}">Apply split</button>
                  <button class="secondary-button ai-content-review-button" type="button" data-issue-action="cancel-edit" data-issue-id="${issue.id}">Cancel</button>
                </div>
              </div>`
            : "";
          const wrongReasonEditor = issue.editMode === "wrong-reason"
            ? `
              <div class="ai-content-review-editor">
                <div class="ai-content-review-inputs ai-content-review-inputs--single">
                  <label class="ai-content-review-field">
                    <span>Correct label</span>
                    <select data-issue-input="corrected-label" data-issue-id="${issue.id}">
                      ${renderLabelOptions(issue.correctedLabel || issue.label)}
                    </select>
                  </label>
                </div>
                <div class="ai-content-review-editor-actions">
                  <button class="secondary-button ai-content-review-button" type="button" data-issue-action="apply-wrong-reason" data-issue-id="${issue.id}">Apply label</button>
                  <button class="secondary-button ai-content-review-button" type="button" data-issue-action="cancel-edit" data-issue-id="${issue.id}">Cancel</button>
                </div>
              </div>`
            : "";

          return `
            <article class="ai-content-issue-card${isSelected ? " is-selected" : ""}" data-issue-card="" data-issue-id="${escapeHtml(issue.id)}" data-review-state="${escapeHtml(issue.reviewStatus)}">
              <div class="ai-content-issue-head">
                <strong>${escapeHtml(getIssueDisplayLabel(issue))}</strong>
                <span>${escapeHtml(formatRangeSummary(issue))}</span>
              </div>
              <div class="ai-content-issue-meta">
                <span class="ai-content-issue-state">${escapeHtml(reviewStateLabel(issue))}</span>
                <span class="ai-content-issue-confidence">${issue.isUserAdded ? "User added" : issue.isLearned ? "Local memory" : `Confidence ${escapeHtml(formatConfidence(issue.confidence))}`}</span>
                <span class="ai-content-issue-feedback">${issue.hasPendingFeedback ? "Needs learn" : "Learned"}</span>
              </div>
              <p>${escapeHtml(issue.reason)}</p>
              ${issue.excerpt ? `<blockquote>${escapeHtml(issue.excerpt)}</blockquote>` : ""}
              <div class="ai-content-review-actions">
                <button class="secondary-button ai-content-review-button" type="button" data-issue-action="accept" data-issue-id="${issue.id}">Keep cut</button>
                <button class="secondary-button ai-content-review-button" type="button" data-issue-action="delete" data-issue-id="${issue.id}">Delete cut</button>
                <button class="secondary-button ai-content-review-button" type="button" data-issue-action="split" data-issue-id="${issue.id}" ${canSplit ? "" : "disabled"}>Split cut</button>
                <button class="secondary-button ai-content-review-button" type="button" data-issue-action="wrong-reason" data-issue-id="${issue.id}">Wrong reason</button>
                <button class="secondary-button ai-content-review-button is-learn" type="button" data-issue-action="learn" data-issue-id="${issue.id}" ${issue.hasPendingFeedback ? "" : "disabled"}>${issue.hasPendingFeedback ? "Learn" : "Learned"}</button>
                <button class="secondary-button ai-content-review-button" type="button" data-issue-action="reset" data-issue-id="${issue.id}" ${canReset ? "" : "disabled"}>Reset</button>
              </div>
              ${splitEditor}
              ${wrongReasonEditor}
            </article>`;
        }).join("");
      } else if (hasAnyIssues) {
        issuesContainer.innerHTML = `<p class="ai-content-empty">No A2 cuts above the current confidence floor (${escapeHtml(formatThreshold())}). Lower it to review more suggestions.</p>`;
      } else {
        issuesContainer.innerHTML = '<p class="ai-content-empty">A2 did not flag any likely spoken mistakes in this transcript.</p>';
      }

      const transcriptLines = transcriptSegments.length > 0
        ? transcriptSegments.map((segment) => `
            <div class="ai-content-transcript-line">
              <span>${escapeHtml(formatDuration(segment.startSeconds))}</span>
              <p>${escapeHtml(segment.text)}</p>
            </div>
          `).join("")
        : `<p class="ai-content-transcript-raw">${escapeHtml(transcriptText)}</p>`;

      transcriptBody.innerHTML = transcriptLines;
      if (transcriptionModel || analysisModel) {
        transcriptModelTag.hidden = false;
        transcriptModelTag.textContent = [transcriptionModel, analysisModel].filter(Boolean).join(" -> ");
      }
    }
  };

  const refreshA2State = ({ forceSync = false } = {}) => {
    syncSelectedRange();
    updateThresholdSummary();
    renderResults();
    syncRanges({ force: forceSync });
    renderOverlay();
    clearButton.hidden = issueItems.length === 0;
    syncActionButtons();
  };

  const clearContentAnalysis = ({ keepStatus = false } = {}) => {
    issueItems = [];
    transcriptSegments = [];
    transcriptText = "";
    transcriptionModel = "";
    analysisModel = "";
    analysisSessionId = "";
    dragState = null;
    addModeEnabled = false;
    selectedRangeRef = null;
    overlay.innerHTML = "";
    refreshA2State({ forceSync: true });
    if (!keepStatus) {
      setStatus("", { hidden: true });
    }
    setFeedbackStatus("", { hidden: true });
  };

  const syncA2BypassState = () => {
    syncToggleLabel();
    panel.classList.toggle("is-bypassed", !enabledToggle.checked);
    modelSelect.disabled = !enabledToggle.checked;
    thresholdSelect.disabled = !enabledToggle.checked;
    memoryModeSelect.disabled = !enabledToggle.checked;
    clearButton.disabled = !enabledToggle.checked;
    forgetMemoryButton.disabled = true;
    if (!enabledToggle.checked) {
      clearContentAnalysis();
    }
    updateAnalyzeButtonState();
    syncActionButtons();
  };

  const persistSettings = () => {
    try {
      window.localStorage.setItem(modelStorageKey, modelSelect.value);
      window.localStorage.setItem(thresholdStorageKey, thresholdSelect.value);
      window.localStorage.setItem(memoryModeStorageKey, memoryModeSelect.value);
    } catch {
    }
  };

  const restoreSettings = () => {
    try {
      const storedModel = window.localStorage.getItem(modelStorageKey);
      if (storedModel && Array.from(modelSelect.options).some((option) => option.value === storedModel)) {
        modelSelect.value = storedModel;
      }

      const storedThreshold = window.localStorage.getItem(thresholdStorageKey);
      if (storedThreshold && Array.from(thresholdSelect.options).some((option) => option.value === storedThreshold)) {
        thresholdSelect.value = storedThreshold;
      }

      const storedMemoryMode = window.localStorage.getItem(memoryModeStorageKey);
      if (storedMemoryMode && Array.from(memoryModeSelect.options).some((option) => option.value === storedMemoryMode)) {
        memoryModeSelect.value = storedMemoryMode;
      }
    } catch {
    }
  };

  const updateIssue = (issueId, update) => {
    const issue = issueItems.find((entry) => entry.id === issueId);
    if (!issue) {
      return null;
    }

    update(issue);
    return issue;
  };

  const saveFeedback = async (issue, action, { note = null, ranges = issue.ranges } = {}) => {
    const file = fileInput.files?.[0];
    if (!file || !analysisSessionId) {
      return;
    }

    const payload = {
      sessionId: analysisSessionId,
      clientFileName: file.name,
      spokenLanguage: languageSelect.value || "auto",
      transcriptionModel,
      analysisModel,
      transcriptText,
      transcriptSegments: transcriptSegments.map((segment) => ({
        startSeconds: segment.startSeconds,
        endSeconds: segment.endSeconds,
        text: segment.text,
      })),
      suggestion: {
        suggestionId: issue.id,
        action,
        label: issue.originalLabel || issue.label,
        correctedLabel: issue.correctedLabel,
        reason: issue.reason,
        excerpt: issue.excerpt,
        confidence: issue.confidence,
        originalStartSeconds: issue.originalStartSeconds,
        originalEndSeconds: issue.originalEndSeconds,
        currentRanges: ranges.map((range) => ({
          startSeconds: Number(range.startSeconds.toFixed(3)),
          endSeconds: Number(range.endSeconds.toFixed(3)),
        })),
        note,
      },
    };

    const response = await fetch("/api/ai/content-feedback", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(payload),
    });

    const result = await response.json().catch(() => null);
    if (!response.ok || !result?.success) {
      throw new Error(result?.message || "Could not save A2 feedback.");
    }
  };

  const buildLearnAction = (issue) => {
    if (issue.isUserAdded) {
      return "added";
    }

    switch (issue.reviewStatus) {
      case "accepted":
        return "accept";
      case "rejected":
        return "reject";
      case "split":
        return "split";
      case "wrong_reason":
        return "wrong_reason";
      default:
        return "learn";
    }
  };

  const buildLearnNote = (issue) => {
    if (issue.isUserAdded && !rangesEqual(issue.ranges, issue.originalRanges)) {
      return `Added a missed cut and adjusted it from ${issue.originalRanges.map((range) => `${formatDuration(range.startSeconds)}-${formatDuration(range.endSeconds)}`).join(" | ")} to ${issue.ranges.map((range) => `${formatDuration(range.startSeconds)}-${formatDuration(range.endSeconds)}`).join(" | ")}.`;
    }

    if (issue.isUserAdded) {
      return "Added a missed A2 cut manually.";
    }

    if (!rangesEqual(issue.ranges, issue.originalRanges)) {
      return `Adjusted from ${issue.originalRanges.map((range) => `${formatDuration(range.startSeconds)}-${formatDuration(range.endSeconds)}`).join(" | ")} to ${issue.ranges.map((range) => `${formatDuration(range.startSeconds)}-${formatDuration(range.endSeconds)}`).join(" | ")}.`;
    }

    if (issue.reviewStatus === "wrong_reason") {
      return issue.correctedLabel
        ? `Range appears useful but the label should be '${issue.correctedLabel}' instead of '${issue.originalLabel || issue.label}'.`
        : "Range appears useful but the reason or label needs correction.";
    }

    if (issue.reviewStatus === "accepted") {
      return "Kept the suggested AI cut without range changes.";
    }

    if (issue.reviewStatus === "rejected") {
      return "Deleted the suggested AI cut.";
    }

    return "Saved the current AI suggestion state for learning.";
  };

  const learnIssue = async (issueId) => {
    const issue = issueItems.find((entry) => entry.id === issueId);
    if (!issue) {
      return;
    }

    try {
      const action = buildLearnAction(issue);
      const ranges = action === "reject" ? [] : issue.ranges;
      await saveFeedback(issue, action, {
        note: buildLearnNote(issue),
        ranges,
      });
      issue.hasPendingFeedback = false;
      refreshA2State({ forceSync: true });
      setFeedbackStatus(`Saved A2 feedback: ${action.replaceAll("_", " ")}.`, { hidden: false });
    } catch (error) {
      setFeedbackStatus(error instanceof Error ? error.message : "Could not save A2 feedback.", { isError: true });
    }
  };

  const markIssueChanged = (issue, nextReviewStatus = "adjusted") => {
    issue.reviewStatus = nextReviewStatus;
    issue.hasPendingFeedback = true;
    issue.isLearned = false;
  };

  const deleteSelectedRange = () => {
    syncSelectedRange();
    if (!selectedRangeRef) {
      return;
    }

    const issue = getIssueById(selectedRangeRef.issueId);
    if (!issue) {
      selectedRangeRef = null;
      refreshA2State({ forceSync: true });
      return;
    }

    const rangeIndex = selectedRangeRef.rangeIndex;
    if (!issue.ranges[rangeIndex]) {
      selectedRangeRef = null;
      refreshA2State({ forceSync: true });
      return;
    }

    if (issue.isUserAdded) {
      if (issue.ranges.length === 1) {
        issueItems = issueItems.filter((entry) => entry.id !== issue.id);
        selectedRangeRef = null;
        refreshA2State({ forceSync: true });
        setFeedbackStatus("Added A2 cut removed.", { hidden: false });
        return;
      }

      issue.ranges = issue.ranges.filter((_, index) => index !== rangeIndex);
      markIssueChanged(issue);
      setSelectedRange(issue.id, Math.min(rangeIndex, issue.ranges.length - 1));
      refreshA2State({ forceSync: true });
      setFeedbackStatus("Selected A2 range removed. Click Learn to save this correction.", { hidden: false });
      return;
    }

    if (issue.ranges.length > 1) {
      issue.ranges = issue.ranges.filter((_, index) => index !== rangeIndex);
      issue.editMode = null;
      markIssueChanged(issue, issue.ranges.length > 1 ? "split" : "adjusted");
      setSelectedRange(issue.id, Math.min(rangeIndex, issue.ranges.length - 1));
      refreshA2State({ forceSync: true });
      setFeedbackStatus("Selected A2 range removed. Click Learn to save this correction.", { hidden: false });
      return;
    }

    issue.editMode = null;
    markIssueChanged(issue, "rejected");
    selectedRangeRef = null;
    refreshA2State({ forceSync: true });
    setFeedbackStatus("Selected A2 cut removed. Click Learn to save this rejection.", { hidden: false });
  };

  const deleteIssueCut = (issueId) => {
    const issue = getIssueById(issueId);
    if (!issue) {
      return;
    }

    if (selectedRangeRef?.issueId === issueId) {
      selectedRangeRef = null;
    }

    if (issue.isUserAdded) {
      issueItems = issueItems.filter((entry) => entry.id !== issueId);
      refreshA2State({ forceSync: true });
      setFeedbackStatus("Added A2 cut deleted.", { hidden: false });
      return;
    }

    issue.editMode = null;
    markIssueChanged(issue, "rejected");
    refreshA2State({ forceSync: true });
    setFeedbackStatus("A2 cut deleted. Click Learn to save this rejection.", { hidden: false });
  };

  const forgetClipMemory = async () => {
    const file = fileInput.files?.[0];
    if (!file || transcriptText.trim().length === 0) {
      setFeedbackStatus("Run A2 once on this clip before forgetting its learned memory.", { isError: true });
      return;
    }

    forgetMemoryButton.disabled = true;
    setFeedbackStatus("Forgetting learned memory for this clip...");

    try {
      const response = await fetch("/api/ai/content-memory/forget", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          clientFileName: file.name,
          transcriptText,
        }),
      });

      const result = await response.json().catch(() => null);
      if (!response.ok || !result?.success) {
        throw new Error(result?.message || "Could not forget learned clip memory.");
      }

      clearContentAnalysis({ keepStatus: true });
      setStatus(`Forgot ${result.removedCount ?? 0} learned feedback record${result?.removedCount === 1 ? "" : "s"} for this clip. Run A2 again to see a fresh pass.`);
      setFeedbackStatus("Clip memory cleared.", { hidden: false });
    } catch (error) {
      setFeedbackStatus(error instanceof Error ? error.message : "Could not forget learned clip memory.", { isError: true });
      syncActionButtons();
    }
  };

  const analyzeContent = async () => {
    const file = fileInput.files?.[0];
    if (!file) {
      setStatus("Choose a video file before running A2 analysis.", { isError: true });
      updateAnalyzeButtonState();
      return;
    }

    const apiKey = apiKeyInput.value.trim();
    if (!apiKey && !hasDefaultServerKey) {
      setStatus("Enter an OpenAI API key or add one to appsettings.Local.json before running A2 analysis.", { isError: true });
      updateAnalyzeButtonState();
      return;
    }

    persistSettings();
    setAnalyzingState(true);
    setStatus(useLearnedMemory()
      ? "Analyzing transcript content with OpenAI..."
      : "Analyzing transcript content with OpenAI in fresh mode...");
    setFeedbackStatus("", { hidden: true });

    const formData = new FormData();
    formData.append("video", file);
    formData.append("apiKey", apiKey);
    formData.append("analysisModel", modelSelect.value);
    formData.append("language", languageSelect.value);
    formData.append("useMemory", useLearnedMemory() ? "true" : "false");

    try {
      const response = await fetch("/api/ai/content-analysis", {
        method: "POST",
        body: formData,
      });

      const payload = await response.json().catch(() => null);
      if (!response.ok || !payload?.success) {
        throw new Error(payload?.message || "A2 analysis failed.");
      }

      if (!enabledToggle.checked) {
        clearContentAnalysis();
        return;
      }

      transcriptionModel = `${payload.transcriptionModel || "whisper-1"}`;
      analysisModel = `${payload.analysisModel || modelSelect.value}`;
      transcriptText = `${payload.transcriptText || ""}`.trim();
      transcriptSegments = Array.isArray(payload.transcriptSegments)
        ? payload.transcriptSegments
            .map((segment) => ({
              startSeconds: Number(segment.startSeconds),
              endSeconds: Number(segment.endSeconds),
              text: `${segment.text || ""}`.trim(),
            }))
            .filter((segment) =>
              Number.isFinite(segment.startSeconds) &&
              Number.isFinite(segment.endSeconds) &&
              segment.endSeconds > segment.startSeconds &&
              segment.text.length > 0)
        : [];
      analysisSessionId = createSessionId();
      addModeEnabled = false;
      selectedRangeRef = null;
      issueItems = Array.isArray(payload.issues)
        ? payload.issues
            .map((issue) => {
              const normalizedRanges = normalizeRanges([issue]);
              return {
                id: createIssueId(),
                originalStartSeconds: Number(issue.startSeconds),
                originalEndSeconds: Number(issue.endSeconds),
                originalRanges: cloneRanges(normalizedRanges),
                originalLabel: `${issue.label || "Retake"}`.trim(),
                label: `${issue.label || "Retake"}`.trim(),
                correctedLabel: null,
                reason: `${issue.reason || "Potential spoken mistake."}`.trim(),
                excerpt: `${issue.excerpt || ""}`.trim(),
                confidence: clamp(Number(issue.confidence) || 0.5, 0, 1),
                ranges: normalizedRanges,
                reviewStatus: issue.isLearned ? "accepted" : "pending",
                editMode: null,
                splitPointSeconds: (Number(issue.startSeconds) + Number(issue.endSeconds)) / 2,
                hasPendingFeedback: !Boolean(issue.isLearned),
                isUserAdded: false,
                isLearned: Boolean(issue.isLearned),
              };
            })
            .filter((issue) => issue.ranges.length > 0)
        : [];

      refreshA2State({ forceSync: true });
      setStatus(payload.summary || payload.message || "A2 analysis completed.");
    } catch (error) {
      clearContentAnalysis({ keepStatus: true });
      setStatus(error instanceof Error ? error.message : "A2 analysis failed.", { isError: true });
    } finally {
      setAnalyzingState(false);
    }
  };

  const beginRangeDrag = (event, handle) => {
    if (dragState) {
      return;
    }

    if (!(handle instanceof HTMLElement)) {
      return;
    }

    const issueId = handle.dataset.issueId;
    const rangeIndex = Number.parseInt(handle.dataset.rangeIndex || "", 10);
    const edge = handle.dataset.aiContentHandle;
    if (!issueId || !Number.isInteger(rangeIndex) || (edge !== "start" && edge !== "end")) {
      return;
    }

    const issue = issueItems.find((entry) => entry.id === issueId);
    if (!issue || !issue.ranges[rangeIndex]) {
      return;
    }

    dragState = {
      mode: "adjust",
      issueId,
      rangeIndex,
      edge,
      pointerId: "pointerId" in event ? event.pointerId : null,
      captureElement: handle,
    };

    if ("pointerId" in event && typeof event.pointerId === "number") {
      handle.setPointerCapture?.(event.pointerId);
    }

    setSelectedRange(issueId, rangeIndex);
    syncActionButtons();
    event.preventDefault();
    event.stopPropagation();
  };

  const beginCreateRange = (event) => {
    if (dragState || !enabledToggle.checked || !analysisSessionId || !waveformDurationSeconds) {
      return;
    }

    const previewRegion = document.createElement("div");
    previewRegion.className = "waveform-ai-content-region is-creating is-selected";
    previewRegion.setAttribute("data-ai-content-range-preview", "");

    const badge = document.createElement("span");
    badge.className = "waveform-ai-content-badge";
    badge.textContent = "ADD";
    previewRegion.appendChild(badge);
    overlay.appendChild(previewRegion);

    const startSeconds = xToSeconds(event.clientX);
    dragState = {
      mode: "create",
      startSeconds,
      endSeconds: startSeconds,
      pointerId: "pointerId" in event ? event.pointerId : null,
      captureElement: overlay,
      previewRegion,
    };

    if ("pointerId" in event && typeof event.pointerId === "number") {
      overlay.setPointerCapture?.(event.pointerId);
    }

    event.preventDefault();
    event.stopPropagation();
  };

  const applyDraggedRange = (event) => {
    if (!dragState || !waveformDurationSeconds) {
      return;
    }

    if (dragState.mode === "create") {
      dragState.endSeconds = xToSeconds(event.clientX);
      const previewStart = Math.min(dragState.startSeconds, dragState.endSeconds);
      const previewEnd = Math.max(dragState.startSeconds, dragState.endSeconds);
      applyOverlayRegionLayout(dragState.previewRegion, {
        startSeconds: previewStart,
        endSeconds: Math.max(previewStart + minimumRangeSeconds, previewEnd),
      });
      return;
    }

    const issue = issueItems.find((entry) => entry.id === dragState.issueId);
    if (!issue) {
      return;
    }

    const range = issue.ranges[dragState.rangeIndex];
    if (!range) {
      return;
    }

    const nextSeconds = xToSeconds(event.clientX);
    if (dragState.edge === "start") {
      range.startSeconds = clamp(nextSeconds, 0, range.endSeconds - minimumRangeSeconds);
    } else {
      range.endSeconds = clamp(nextSeconds, range.startSeconds + minimumRangeSeconds, waveformDurationSeconds);
    }

    markIssueChanged(issue);
    const region = overlay.querySelector(`[data-ai-content-range][data-issue-id="${dragState.issueId}"][data-range-index="${dragState.rangeIndex}"]`);
    if (region instanceof HTMLElement) {
      applyOverlayRegionLayout(region, range);
      region.classList.add("is-pending-feedback");
    }
  };

  const finishRangeDrag = () => {
    if (!dragState) {
      return;
    }

    if (typeof dragState.pointerId === "number") {
      dragState.captureElement.releasePointerCapture?.(dragState.pointerId);
    }

    if (dragState.mode === "create") {
      dragState.previewRegion.remove();

      const startSeconds = Math.min(dragState.startSeconds, dragState.endSeconds);
      const endSeconds = Math.max(dragState.startSeconds, dragState.endSeconds);
      dragState = null;

      if (endSeconds - startSeconds < minimumRangeSeconds) {
        refreshA2State({ forceSync: true });
        return;
      }

      const issue = createUserAddedIssue(startSeconds, endSeconds);
      issueItems.push(issue);
      addModeEnabled = false;
      setSelectedRange(issue.id, 0);
      refreshA2State({ forceSync: true });
      setFeedbackStatus("Added a missed A2 cut. Drag handles if needed, then click Learn to save it.", { hidden: false });
      return;
    }

    dragState = null;
    refreshA2State({ forceSync: true });
    setFeedbackStatus("Range adjusted. Click Learn to save this correction.", { hidden: false });
  };

  restoreSettings();
  syncA2BypassState();
  updateAnalyzeButtonState();
  clearButton.hidden = true;
  results.hidden = true;
  syncActionButtons();

  analyzeButton.addEventListener("click", () => {
    void analyzeContent();
  });

  addButton.addEventListener("click", () => {
    if (addButton.disabled) {
      return;
    }

    addModeEnabled = !addModeEnabled;
    if (addModeEnabled) {
      selectedRangeRef = null;
      setFeedbackStatus("Drag on the waveform to add a missed A2 cut.", { hidden: false });
    } else {
      setFeedbackStatus("", { hidden: true });
    }

    refreshA2State({ forceSync: true });
  });

  deleteSelectedButton.addEventListener("click", () => {
    deleteSelectedRange();
  });

  clearButton.addEventListener("click", () => {
    clearContentAnalysis();
  });

  enabledToggle.addEventListener("change", () => {
    syncA2BypassState();
  });

  modelSelect.addEventListener("change", persistSettings);
  memoryModeSelect.addEventListener("change", () => {
    persistSettings();
    syncActionButtons();
  });

  thresholdSelect.addEventListener("change", () => {
    persistSettings();
    refreshA2State({ forceSync: true });
  });

  forgetMemoryButton.addEventListener("click", () => {
    void forgetClipMemory();
  });

  fileInput.addEventListener("change", () => {
    clearContentAnalysis();
    updateAnalyzeButtonState();
  });

  issuesContainer.addEventListener("input", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement) && !(target instanceof HTMLSelectElement)) {
      return;
    }

    const issueId = target.dataset.issueId;
    if (!issueId) {
      return;
    }

    if (target.dataset.issueInput === "split" && target instanceof HTMLInputElement) {
      const nextValue = Number(target.value);
      if (!Number.isFinite(nextValue)) {
        return;
      }

      updateIssue(issueId, (issue) => {
        issue.splitPointSeconds = nextValue;
      });
      return;
    }

    if (target.dataset.issueInput === "corrected-label" && target instanceof HTMLSelectElement) {
      updateIssue(issueId, (issue) => {
        issue.correctedLabel = target.value.trim() || issue.label;
      });
    }
  });

  issuesContainer.addEventListener("click", (event) => {
    const button = event.target instanceof Element
      ? event.target.closest("[data-issue-action]")
      : null;

    if (button instanceof HTMLButtonElement) {
      const issueId = button.dataset.issueId;
      const action = button.dataset.issueAction;
      if (!issueId || !action) {
        return;
      }

      if (action === "learn") {
        void learnIssue(issueId);
        return;
      }

      if (action === "accept") {
        updateIssue(issueId, (issue) => {
          markIssueChanged(issue, "accepted");
        });
        refreshA2State({ forceSync: true });
        return;
      }

      if (action === "reject" || action === "delete") {
        deleteIssueCut(issueId);
        return;
      }

      if (action === "wrong-reason") {
        updateIssue(issueId, (issue) => {
          issue.editMode = "wrong-reason";
          issue.correctedLabel = issue.correctedLabel || issue.label;
        });
        renderResults();
        return;
      }

      if (action === "split") {
        updateIssue(issueId, (issue) => {
          const [currentRange] = issue.ranges;
          if (!currentRange) {
            return;
          }

          issue.editMode = "split";
          issue.splitPointSeconds = (currentRange.startSeconds + currentRange.endSeconds) / 2;
        });
        renderResults();
        return;
      }

      if (action === "cancel-edit") {
        updateIssue(issueId, (issue) => {
          issue.editMode = null;
          if (issue.reviewStatus === "pending") {
            issue.correctedLabel = null;
          }
        });
        renderResults();
        return;
      }

      if (action === "apply-wrong-reason") {
        const issue = issueItems.find((entry) => entry.id === issueId);
        if (!issue) {
          return;
        }

        const nextLabel = `${issue.correctedLabel || ""}`.trim();
        if (!nextLabel) {
          setFeedbackStatus("Choose the correct label before applying the correction.", { isError: true });
          return;
        }

        issue.editMode = null;
        markIssueChanged(issue, "wrong_reason");
        refreshA2State({ forceSync: true });
        setFeedbackStatus(`Corrected label set to ${nextLabel}. Click Learn to save it.`, { hidden: false });
        return;
      }

      if (action === "apply-split") {
        const issue = issueItems.find((entry) => entry.id === issueId);
        if (!issue || issue.ranges.length !== 1) {
          return;
        }

        const [currentRange] = issue.ranges;
        const splitPoint = Number(issue.splitPointSeconds);
        if (!Number.isFinite(splitPoint) ||
            splitPoint <= currentRange.startSeconds + minimumRangeSeconds ||
            splitPoint >= currentRange.endSeconds - minimumRangeSeconds) {
          setFeedbackStatus("Split point must stay inside the selected range.", { isError: true });
          return;
        }

        issue.ranges = normalizeRanges([
          {
            startSeconds: currentRange.startSeconds,
            endSeconds: splitPoint,
          },
          {
            startSeconds: splitPoint,
            endSeconds: currentRange.endSeconds,
          },
        ]);
        issue.editMode = null;
        markIssueChanged(issue, "split");
        refreshA2State({ forceSync: true });
        setFeedbackStatus("Split applied. Click Learn to save this correction.", { hidden: false });
        return;
      }

      if (action === "reset") {
        updateIssue(issueId, (issue) => {
          issue.ranges = cloneRanges(issue.originalRanges);
          issue.label = issue.originalLabel;
          issue.correctedLabel = null;
          issue.reviewStatus = "pending";
          issue.editMode = null;
          issue.hasPendingFeedback = true;
        });
        refreshA2State({ forceSync: true });
        setFeedbackStatus("A2 range reset to the original suggestion. Click Learn to save if needed.", { hidden: false });
      }

      return;
    }

    const issueCard = event.target instanceof Element
      ? event.target.closest("[data-issue-card]")
      : null;
    if (!(issueCard instanceof HTMLElement)) {
      return;
    }

    const selectedIssueId = issueCard.dataset.issueId;
    if (!selectedIssueId) {
      return;
    }

    if (setSelectedRange(selectedIssueId, 0)) {
      refreshA2State({ forceSync: true });
    }
  });

  overlay.addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) {
      return;
    }

    if (target.closest("[data-ai-content-handle]")) {
      event.preventDefault();
      event.stopPropagation();
      return;
    }

    const region = target.closest("[data-ai-content-range]");
    if (region instanceof HTMLElement) {
      const issueId = region.dataset.issueId;
      const rangeIndex = Number.parseInt(region.dataset.rangeIndex || "", 10);
      if (issueId && Number.isInteger(rangeIndex)) {
        setSelectedRange(issueId, rangeIndex);
        refreshA2State({ forceSync: true });
      }

      event.preventDefault();
      event.stopPropagation();
      return;
    }

    if (addModeEnabled) {
      event.preventDefault();
      event.stopPropagation();
      return;
    }

    if (selectedRangeRef !== null) {
      selectedRangeRef = null;
      refreshA2State({ forceSync: true });
    }
  });

  overlay.addEventListener("pointerdown", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) {
      return;
    }

    const handle = target.closest("[data-ai-content-handle]");
    if (handle instanceof HTMLElement) {
      beginRangeDrag(event, handle);
      return;
    }

    if (addModeEnabled && event.button === 0 && target === overlay) {
      beginCreateRange(event);
    }
  });

  overlay.addEventListener("mousedown", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) {
      return;
    }

    const handle = target.closest("[data-ai-content-handle]");
    if (handle instanceof HTMLElement && event.button === 0) {
      beginRangeDrag(event, handle);
      return;
    }

    if (addModeEnabled && event.button === 0 && target === overlay) {
      beginCreateRange(event);
    }
  });

  window.addEventListener("pointermove", (event) => {
    applyDraggedRange(event);
  });

  window.addEventListener("mousemove", (event) => {
    applyDraggedRange(event);
  });

  window.addEventListener("pointerup", () => {
    finishRangeDrag();
  });

  window.addEventListener("pointercancel", () => {
    finishRangeDrag();
  });

  window.addEventListener("mouseup", () => {
    finishRangeDrag();
  });

  form.addEventListener("strippr:editor-reset", () => {
    enabledToggle.checked = false;
    syncA2BypassState();
    clearContentAnalysis();
    updateAnalyzeButtonState();
  });

  form.addEventListener("strippr:waveform-state", (event) => {
    const detail = event.detail || {};
    waveformDurationSeconds = Number(detail.durationSeconds) || 0;
    isWaveformReady = Boolean(detail.ready) && !Boolean(detail.hidden);
    renderOverlay();
    updateAnalyzeButtonState();
  });

  if ("ResizeObserver" in window) {
    const resizeObserver = new ResizeObserver(() => {
      renderOverlay();
    });
    resizeObserver.observe(waveformStage);
  }
})();
