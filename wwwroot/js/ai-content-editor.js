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
  const spinner = form.querySelector("[data-ai-content-spinner]");
  const clearButton = form.querySelector("[data-ai-content-clear]");
  const status = form.querySelector("[data-ai-content-status]");
  const modelSelect = form.querySelector("[data-ai-content-model]");
  const enabledToggle = form.querySelector("[data-ai-content-enabled]");
  const results = form.querySelector("[data-ai-content-results]");
  const issuesContainer = form.querySelector("[data-ai-content-issues]");
  const transcriptBody = form.querySelector("[data-ai-content-transcript-body]");
  const transcriptModelTag = form.querySelector("[data-ai-content-transcript-model]");
  const overlay = form.querySelector("[data-ai-content-waveform-overlay]");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const panel = form.querySelector(".ai-content-panel");

  if (
    !(fileInput instanceof HTMLInputElement) ||
    !(apiKeyInput instanceof HTMLInputElement) ||
    !(languageSelect instanceof HTMLSelectElement) ||
    !(analyzeButton instanceof HTMLButtonElement) ||
    !(analyzeLabel instanceof HTMLElement) ||
    !(spinner instanceof HTMLElement) ||
    !(clearButton instanceof HTMLButtonElement) ||
    !(status instanceof HTMLElement) ||
    !(modelSelect instanceof HTMLSelectElement) ||
    !(enabledToggle instanceof HTMLInputElement) ||
    !(results instanceof HTMLElement) ||
    !(issuesContainer instanceof HTMLElement) ||
    !(transcriptBody instanceof HTMLElement) ||
    !(transcriptModelTag instanceof HTMLElement) ||
    !(overlay instanceof HTMLElement) ||
    !(waveformPanel instanceof HTMLElement) ||
    !(waveformStage instanceof HTMLElement) ||
    !(panel instanceof HTMLElement)
  ) {
    return;
  }

  const modelStorageKey = "strippr.ai.content.model.v2";
  const hasDefaultServerKey = panel.dataset.aiContentDefaultKeyAvailable === "true";
  let isAnalyzing = false;
  let waveformDurationSeconds = 0;
  let isWaveformReady = false;
  let issueRanges = [];
  let issueItems = [];
  let transcriptSegments = [];
  let transcriptText = "";
  let transcriptionModel = "";
  let analysisModel = "";
  let lastPublishedRangesKey = "";

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  const getStageWidth = () => waveformStage.clientWidth || Number.parseFloat(waveformStage.style.width) || 0;
  const secondsToX = (seconds) => waveformDurationSeconds > 0
    ? clamp(seconds / waveformDurationSeconds, 0, 1) * getStageWidth()
    : 0;

  const formatDuration = (seconds) => {
    const safeSeconds = Math.max(0, seconds);
    const wholeSeconds = Math.floor(safeSeconds);
    const minutes = Math.floor(wholeSeconds / 60).toString().padStart(2, "0");
    const remainingSeconds = (wholeSeconds % 60).toString().padStart(2, "0");
    const hundredths = Math.floor((safeSeconds - wholeSeconds) * 100).toString().padStart(2, "0");
    return `${minutes}:${remainingSeconds}.${hundredths}`;
  };

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

  const setStatus = (message, { isError = false, hidden = false } = {}) => {
    status.hidden = hidden || !message;
    status.textContent = hidden ? "" : message;
    status.classList.toggle("is-error", isError && !hidden);
  };

  const isA2Enabled = () => enabledToggle.checked;

  const syncToggleLabel = () => {
    const label = enabledToggle.parentElement?.querySelector(".field-toggle__label");
    if (label instanceof HTMLElement) {
      label.textContent = enabledToggle.checked ? "On" : "Off";
    }
  };

  const updateAnalyzeButtonState = () => {
    analyzeButton.disabled =
      !isA2Enabled() ||
      isAnalyzing ||
      !fileInput.files?.[0] ||
      (!apiKeyInput.value.trim() && !hasDefaultServerKey);
  };

  const setAnalyzingState = (value) => {
    isAnalyzing = value;
    spinner.hidden = !value;
    analyzeLabel.textContent = value ? "Analyzing..." : "Analyze content";
    updateAnalyzeButtonState();
  };

  const syncRanges = ({ force = false } = {}) => {
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

  const renderOverlay = () => {
    if (!isA2Enabled() || !isWaveformReady || waveformPanel.hidden || issueRanges.length === 0) {
      overlay.innerHTML = "";
      clearButton.hidden = !isA2Enabled() || issueRanges.length === 0;
      return;
    }

    const stageWidth = getStageWidth();
    if (stageWidth <= 0 || waveformDurationSeconds <= 0) {
      overlay.innerHTML = "";
      clearButton.hidden = issueRanges.length === 0;
      return;
    }

    overlay.innerHTML = "";
    issueItems.forEach((issue) => {
      const startX = secondsToX(issue.startSeconds);
      const endX = secondsToX(issue.endSeconds);
      const region = document.createElement("div");
      region.className = "waveform-ai-content-region";
      region.style.left = `${startX}px`;
      region.style.width = `${Math.max(2, endX - startX)}px`;
      region.title = `${issue.label} ${formatDuration(issue.startSeconds)}-${formatDuration(issue.endSeconds)}: ${issue.reason}`;
      overlay.appendChild(region);
    });

    clearButton.hidden = false;
  };

  const renderResults = () => {
    const hasTranscript = transcriptSegments.length > 0 || transcriptText.trim().length > 0;
    const hasIssues = issueItems.length > 0;

    results.hidden = !hasTranscript && !hasIssues;
    issuesContainer.innerHTML = "";
    transcriptBody.innerHTML = "";
    transcriptModelTag.hidden = true;

    if (!results.hidden) {
      if (hasIssues) {
        issuesContainer.innerHTML = issueItems.map((issue) => `
          <article class="ai-content-issue-card">
            <div class="ai-content-issue-head">
              <strong>${issue.label}</strong>
              <span>${formatDuration(issue.startSeconds)} - ${formatDuration(issue.endSeconds)}</span>
            </div>
            <p>${issue.reason}</p>
            ${issue.excerpt ? `<blockquote>${issue.excerpt}</blockquote>` : ""}
          </article>
        `).join("");
      } else {
        issuesContainer.innerHTML = '<p class="ai-content-empty">A2 did not flag any likely spoken mistakes in this transcript.</p>';
      }

      const transcriptLines = transcriptSegments.length > 0
        ? transcriptSegments.map((segment) => `
            <div class="ai-content-transcript-line">
              <span>${formatDuration(segment.startSeconds)}</span>
              <p>${segment.text}</p>
            </div>
          `).join("")
        : `<p class="ai-content-transcript-raw">${transcriptText}</p>`;

      transcriptBody.innerHTML = transcriptLines;

      if (transcriptionModel || analysisModel) {
        transcriptModelTag.hidden = false;
        transcriptModelTag.textContent = [transcriptionModel, analysisModel].filter(Boolean).join(" -> ");
      }
    }
  };

  const clearContentAnalysis = ({ keepStatus = false } = {}) => {
    issueRanges = [];
    issueItems = [];
    transcriptSegments = [];
    transcriptText = "";
    transcriptionModel = "";
    analysisModel = "";
    overlay.innerHTML = "";
    renderResults();
    syncRanges({ force: true });
    clearButton.hidden = true;
    if (!keepStatus) {
      setStatus("", { hidden: true });
    }
  };

  const syncA2BypassState = () => {
    syncToggleLabel();
    panel.classList.toggle("is-bypassed", !isA2Enabled());
    modelSelect.disabled = !isA2Enabled();
    clearButton.disabled = !isA2Enabled();
    if (!isA2Enabled()) {
      clearContentAnalysis();
    }
    updateAnalyzeButtonState();
  };

  const persistSettings = () => {
    try {
      window.localStorage.setItem(modelStorageKey, modelSelect.value);
    } catch {
    }
  };

  const restoreSettings = () => {
    try {
      const storedModel = window.localStorage.getItem(modelStorageKey);
      if (storedModel && Array.from(modelSelect.options).some((option) => option.value === storedModel)) {
        modelSelect.value = storedModel;
      }
    } catch {
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
    setStatus("Analyzing transcript content with OpenAI...");

    const formData = new FormData();
    formData.append("video", file);
    formData.append("apiKey", apiKey);
    formData.append("analysisModel", modelSelect.value);
    formData.append("language", languageSelect.value);

    try {
      const response = await fetch("/api/ai/content-analysis", {
        method: "POST",
        body: formData,
      });

      const payload = await response.json().catch(() => null);
      if (!response.ok || !payload?.success) {
        throw new Error(payload?.message || "A2 analysis failed.");
      }

      if (!isA2Enabled()) {
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
      issueItems = Array.isArray(payload.issues)
        ? payload.issues
            .map((issue) => ({
              startSeconds: Number(issue.startSeconds),
              endSeconds: Number(issue.endSeconds),
              label: `${issue.label || "Retake"}`.trim(),
              reason: `${issue.reason || "Potential spoken mistake."}`.trim(),
              excerpt: `${issue.excerpt || ""}`.trim(),
            }))
            .filter((issue) =>
              Number.isFinite(issue.startSeconds) &&
              Number.isFinite(issue.endSeconds) &&
              issue.endSeconds > issue.startSeconds)
        : [];
      issueRanges = normalizeRanges(issueItems);

      renderResults();
      syncRanges({ force: true });
      renderOverlay();
      setStatus(payload.summary || payload.message || "A2 analysis completed.");
    } catch (error) {
      clearContentAnalysis({ keepStatus: true });
      setStatus(error instanceof Error ? error.message : "A2 analysis failed.", { isError: true });
    } finally {
      setAnalyzingState(false);
    }
  };

  restoreSettings();
  syncA2BypassState();
  updateAnalyzeButtonState();
  clearButton.hidden = true;
  results.hidden = true;

  analyzeButton.addEventListener("click", () => {
    void analyzeContent();
  });

  clearButton.addEventListener("click", () => {
    clearContentAnalysis();
  });

  enabledToggle.addEventListener("change", () => {
    syncA2BypassState();
  });

  modelSelect.addEventListener("change", persistSettings);

  fileInput.addEventListener("change", () => {
    clearContentAnalysis();
    updateAnalyzeButtonState();
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
