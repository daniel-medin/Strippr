(() => {
  const form = document.querySelector("[data-processing-form]");
  if (!form) {
    return;
  }

  const fileInput = form.querySelector("input[type='file']");
  const apiKeyInput = form.querySelector("[data-ai-api-key]");
  const languageSelect = form.querySelector("[data-ai-language]");
  const analysisModelSelect = form.querySelector("[data-ai-content-model]");
  const enabledToggle = form.querySelector("[data-ai-auto-enabled]");
  const analyzeButton = form.querySelector("[data-ai-auto-analyze]");
  const analyzeLabel = form.querySelector("[data-ai-auto-analyze-label]");
  const spinner = form.querySelector("[data-ai-auto-spinner]");
  const clearButton = form.querySelector("[data-ai-auto-clear]");
  const status = form.querySelector("[data-ai-auto-status]");
  const results = form.querySelector("[data-ai-auto-results]");
  const summary = form.querySelector("[data-ai-auto-summary]");
  const targetTextInput = form.querySelector("[data-ai-auto-target-text]");
  const targetBody = form.querySelector("[data-ai-auto-target-body]");
  const resultBody = form.querySelector("[data-ai-auto-result-body]");
  const modelTag = form.querySelector("[data-ai-auto-model]");
  const hiddenInput = form.querySelector("[data-ai-auto-cut-ranges]");
  const overlay = form.querySelector("[data-ai-auto-waveform-overlay]");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const panel = form.querySelector(".ai-autocut-panel");

  if (
    !(fileInput instanceof HTMLInputElement) ||
    !(apiKeyInput instanceof HTMLInputElement) ||
    !(languageSelect instanceof HTMLSelectElement) ||
    !(analysisModelSelect instanceof HTMLSelectElement) ||
    !(enabledToggle instanceof HTMLInputElement) ||
    !(analyzeButton instanceof HTMLButtonElement) ||
    !(analyzeLabel instanceof HTMLElement) ||
    !(spinner instanceof HTMLElement) ||
    !(clearButton instanceof HTMLButtonElement) ||
    !(status instanceof HTMLElement) ||
    !(results instanceof HTMLElement) ||
    !(summary instanceof HTMLElement) ||
    !(targetTextInput instanceof HTMLTextAreaElement) ||
    !(targetBody instanceof HTMLElement) ||
    !(resultBody instanceof HTMLElement) ||
    !(modelTag instanceof HTMLElement) ||
    !(hiddenInput instanceof HTMLInputElement) ||
    !(overlay instanceof HTMLElement) ||
    !(waveformPanel instanceof HTMLElement) ||
    !(waveformStage instanceof HTMLElement) ||
    !(panel instanceof HTMLElement)
  ) {
    return;
  }

  const hasDefaultServerKey = panel.dataset.aiAutoDefaultKeyAvailable === "true";
  let isAnalyzing = false;
  let waveformDurationSeconds = 0;
  let isWaveformReady = false;
  let issueRanges = [];
  let summaryText = "";
  let targetTranscript = "";
  let resultTranscript = "";
  let modelName = "";
  let lastPublishedRangesKey = "";

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  const getStageWidth = () => waveformStage.clientWidth || Number.parseFloat(waveformStage.style.width) || 0;
  const secondsToX = (seconds) => waveformDurationSeconds > 0
    ? clamp(seconds / waveformDurationSeconds, 0, 1) * getStageWidth()
    : 0;

  const normalizeRanges = (ranges) =>
    Array.isArray(ranges)
      ? ranges
          .map((range) => ({
            startSeconds: Number(range.startSeconds),
            endSeconds: Number(range.endSeconds),
            label: `${range.label ?? "A3"}`.trim() || "A3",
            reason: `${range.reason ?? ""}`.trim(),
            confidence: Number(range.confidence),
          }))
          .filter((range) =>
            Number.isFinite(range.startSeconds) &&
            Number.isFinite(range.endSeconds) &&
            range.endSeconds > range.startSeconds)
          .sort((left, right) => left.startSeconds - right.startSeconds || left.endSeconds - right.endSeconds)
      : [];

  const syncToggleLabel = () => {
    const label = enabledToggle.parentElement?.querySelector(".field-toggle__label");
    if (label instanceof HTMLElement) {
      label.textContent = enabledToggle.checked ? "On" : "Off";
    }
  };

  const isEnabled = () => enabledToggle.checked;

  const setStatus = (message, { isError = false, hidden = false } = {}) => {
    status.hidden = hidden || !message;
    status.textContent = hidden ? "" : message;
    status.classList.toggle("is-error", isError && !hidden);
  };

  const updateAnalyzeButtonState = () => {
    analyzeButton.disabled =
      !isEnabled() ||
      isAnalyzing ||
      !fileInput.files?.[0] ||
      (!apiKeyInput.value.trim() && !hasDefaultServerKey);
  };

  const setAnalyzingState = (value) => {
    isAnalyzing = value;
    spinner.hidden = !value;
    analyzeLabel.textContent = value ? "Analyzing..." : "Auto cut to text";
    updateAnalyzeButtonState();
  };

  const syncHiddenInput = () => {
    hiddenInput.value = JSON.stringify(issueRanges.map((range) => ({
      startSeconds: Number(range.startSeconds.toFixed(3)),
      endSeconds: Number(range.endSeconds.toFixed(3)),
    })));
  };

  const emitRangesChanged = ({ force = false } = {}) => {
    syncHiddenInput();
    if (!force && hiddenInput.value === lastPublishedRangesKey) {
      return;
    }

    lastPublishedRangesKey = hiddenInput.value;
    form.dispatchEvent(new CustomEvent("strippr:ai-auto-ranges-changed", {
      detail: {
        ranges: issueRanges.map((range) => ({
          startSeconds: range.startSeconds,
          endSeconds: range.endSeconds,
        })),
      },
    }));
  };

  const renderOverlay = () => {
    if (!isEnabled() || !issueRanges.length || !isWaveformReady || waveformPanel.hidden) {
      overlay.innerHTML = "";
      clearButton.hidden = issueRanges.length === 0;
      return;
    }

    const stageWidth = getStageWidth();
    if (stageWidth <= 0 || waveformDurationSeconds <= 0) {
      overlay.innerHTML = "";
      clearButton.hidden = issueRanges.length === 0;
      return;
    }

    overlay.innerHTML = "";
    issueRanges.forEach((range) => {
      const startX = secondsToX(range.startSeconds);
      const endX = secondsToX(range.endSeconds);
      const region = document.createElement("div");
      region.className = "waveform-ai-auto-region";
      region.style.left = `${startX}px`;
      region.style.width = `${Math.max(2, endX - startX)}px`;
      region.title = `${range.label} ${range.startSeconds.toFixed(2)}s - ${range.endSeconds.toFixed(2)}s${range.reason ? ` | ${range.reason}` : ""}`;

      const badge = document.createElement("span");
      badge.className = "waveform-ai-auto-badge";
      badge.textContent = "A3";
      region.appendChild(badge);
      overlay.appendChild(region);
    });

    clearButton.hidden = false;
  };

  const renderResults = () => {
    summary.hidden = !summaryText;
    summary.textContent = summaryText;
    targetBody.textContent = targetTranscript || "A3 did not infer a target read.";
    resultBody.textContent = resultTranscript || "A3 has not rendered a post-cut transcript yet.";
    modelTag.hidden = !modelName;
    modelTag.textContent = modelName;
    results.hidden = !summaryText && !targetTranscript && !resultTranscript;
  };

  const clearAnalysis = ({ keepStatus = false } = {}) => {
    issueRanges = [];
    summaryText = "";
    targetTranscript = "";
    resultTranscript = "";
    modelName = "";
    overlay.innerHTML = "";
    renderResults();
    emitRangesChanged({ force: true });
    clearButton.hidden = true;
    if (!keepStatus) {
      setStatus("", { hidden: true });
    }
  };

  const syncBypassState = () => {
    syncToggleLabel();
    panel.classList.toggle("is-bypassed", !isEnabled());
    targetTextInput.disabled = !isEnabled();
    clearButton.disabled = !isEnabled();

    if (!isEnabled()) {
      clearAnalysis();
    }

    updateAnalyzeButtonState();
  };

  analyzeButton.addEventListener("click", async () => {
    const video = fileInput.files?.[0];
    if (!video) {
      setStatus("Choose a video file before running A3.", { isError: true });
      return;
    }

    setAnalyzingState(true);
    setStatus("A3 is building a clean target read and cut plan...", { hidden: false });

    try {
      const formData = new FormData();
      formData.append("video", video);
      formData.append("apiKey", apiKeyInput.value.trim());
      formData.append("analysisModel", analysisModelSelect.value);
      formData.append("language", languageSelect.value);
      formData.append("targetText", targetTextInput.value);

      const response = await fetch("/api/ai/auto-cut-analysis", {
        method: "POST",
        body: formData,
      });
      const payload = await response.json();
      if (!response.ok || !payload.success) {
        throw new Error(payload.message || "A3 analysis failed.");
      }

      issueRanges = normalizeRanges(payload.issues);
      summaryText = `${payload.summary ?? ""}`.trim();
      targetTranscript = `${payload.targetTranscript ?? ""}`.trim();
      resultTranscript = `${payload.resultTranscript ?? ""}`.trim();
      modelName = [payload.transcriptionModel, payload.analysisModel]
        .filter((value) => typeof value === "string" && value.trim().length > 0)
        .join(" -> ");

      renderResults();
      renderOverlay();
      emitRangesChanged({ force: true });
      setStatus(payload.message || (issueRanges.length > 0
        ? `A3 found ${issueRanges.length} combined cut ${issueRanges.length === 1 ? "range" : "ranges"}.`
        : "A3 found no combined cut ranges."), { hidden: false });
    } catch (error) {
      clearAnalysis({ keepStatus: true });
      setStatus(error instanceof Error ? error.message : "A3 analysis failed.", { isError: true });
    } finally {
      setAnalyzingState(false);
      renderOverlay();
    }
  });

  clearButton.addEventListener("click", () => {
    clearAnalysis();
  });

  enabledToggle.addEventListener("change", () => {
    syncBypassState();
    renderOverlay();
  });

  fileInput.addEventListener("change", () => {
    clearAnalysis();
    updateAnalyzeButtonState();
  });

  apiKeyInput.addEventListener("input", () => {
    updateAnalyzeButtonState();
  });

  form.addEventListener("strippr:waveform-state", (event) => {
    const detail = event.detail || {};
    waveformDurationSeconds = Number(detail.durationSeconds) || 0;
    isWaveformReady = Boolean(detail.ready) && !detail.hidden;
    renderOverlay();
  });

  form.addEventListener("strippr:editor-reset", () => {
    clearAnalysis();
    updateAnalyzeButtonState();
  });

  syncBypassState();
  clearButton.hidden = true;
  renderResults();
  emitRangesChanged({ force: true });
})();
