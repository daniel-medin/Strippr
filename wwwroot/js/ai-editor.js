(() => {
  const form = document.querySelector("[data-processing-form]");
  if (!form) {
    return;
  }

  const fileInput = form.querySelector("input[type='file']");
  const silenceSlider = form.querySelector("[data-silence-slider]");
  const apiKeyInput = form.querySelector("[data-ai-api-key]");
  const modelSelect = form.querySelector("[data-ai-model]");
  const languageSelect = form.querySelector("[data-ai-language]");
  const aiEnabledToggle = form.querySelector("[data-ai-enabled]");
  const analyzeButton = form.querySelector("[data-ai-analyze]");
  const analyzeLabel = form.querySelector("[data-ai-analyze-label]");
  const spinner = form.querySelector(".ai-analysis-spinner");
  const clearButton = form.querySelector("[data-ai-clear]");
  const status = form.querySelector("[data-ai-status]");
  const aiCutRangesInput = form.querySelector("[data-ai-cut-ranges]");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const aiOverlay = form.querySelector("[data-ai-waveform-overlay]");
  const aiPanel = form.querySelector(".ai-analysis-panel");

  if (
    !(fileInput instanceof HTMLInputElement) ||
    !(apiKeyInput instanceof HTMLInputElement) ||
    !(modelSelect instanceof HTMLSelectElement) ||
    !(languageSelect instanceof HTMLSelectElement) ||
    !(aiEnabledToggle instanceof HTMLInputElement) ||
    !(analyzeButton instanceof HTMLButtonElement) ||
    !(analyzeLabel instanceof HTMLElement) ||
    !(clearButton instanceof HTMLButtonElement) ||
    !(status instanceof HTMLElement) ||
    !(aiCutRangesInput instanceof HTMLInputElement) ||
    !(waveformPanel instanceof HTMLElement) ||
    !(waveformStage instanceof HTMLElement) ||
    !(aiOverlay instanceof HTMLElement) ||
    !(aiPanel instanceof HTMLElement)
  ) {
    return;
  }

  const modelStorageKey = "strippr.ai.model";
  const languageStorageKey = "strippr.ai.language";
  const hasDefaultServerKey = aiPanel.dataset.aiDefaultKeyAvailable === "true";
  const defaultServerModel = aiPanel.dataset.aiDefaultModel || "whisper-1";
  let aiRanges = [];
  let aiSourceRanges = [];
  let analysisDurationSeconds = 0;
  let waveformDurationSeconds = 0;
  let acousticSilenceRanges = [];
  let isWaveformReady = false;
  let isAnalyzing = false;
  let lastPublishedRangesKey = "";

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  const getRenderDurationSeconds = () => waveformDurationSeconds > 0 ? waveformDurationSeconds : analysisDurationSeconds;
  const getStageWidth = () => waveformStage.clientWidth || Number.parseFloat(waveformStage.style.width) || 0;
  const secondsToX = (seconds) => {
    const durationSeconds = getRenderDurationSeconds();
    const stageWidth = getStageWidth();
    return durationSeconds > 0 && stageWidth > 0
      ? clamp(seconds / durationSeconds, 0, 1) * stageWidth
      : 0;
  };

  const setStatus = (message, { isError = false, hidden = false } = {}) => {
    status.hidden = hidden || !message;
    status.textContent = hidden ? "" : message;
    status.classList.toggle("is-error", isError && !hidden);
  };

  const isAiEnabled = () => aiEnabledToggle.checked;

  const syncToggleLabel = () => {
    const label = aiEnabledToggle.parentElement?.querySelector(".field-toggle__label");
    if (label instanceof HTMLElement) {
      label.textContent = aiEnabledToggle.checked ? "On" : "Off";
    }
  };

  const updateAnalyzeButtonState = () => {
    analyzeButton.disabled =
      !isAiEnabled() ||
      isAnalyzing ||
      !fileInput.files?.[0] ||
      (!apiKeyInput.value.trim() && !hasDefaultServerKey);
  };

  const setAnalyzingState = (value) => {
    isAnalyzing = value;
    spinner.hidden = !value;
    analyzeLabel.textContent = value ? "Analyzing..." : "Analyze with AI";
    updateAnalyzeButtonState();
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

  const normalizeMergedRanges = (ranges) => normalizeRanges(ranges).reduce((merged, currentRange) => {
    const previousRange = merged[merged.length - 1];
    if (!previousRange || currentRange.startSeconds > previousRange.endSeconds) {
      merged.push({ ...currentRange });
      return merged;
    }

    previousRange.endSeconds = Math.max(previousRange.endSeconds, currentRange.endSeconds);
    return merged;
  }, []);

  const snapRangesToAcousticSilence = (sourceRanges, allowedRanges) => {
    if (!sourceRanges.length || !allowedRanges.length) {
      return [];
    }

    const snappedRanges = [];
    let allowedIndex = 0;

    sourceRanges.forEach((sourceRange) => {
      while (allowedIndex < allowedRanges.length && allowedRanges[allowedIndex].endSeconds <= sourceRange.startSeconds) {
        allowedIndex += 1;
      }

      let currentAllowedIndex = allowedIndex;
      while (currentAllowedIndex < allowedRanges.length && allowedRanges[currentAllowedIndex].startSeconds < sourceRange.endSeconds) {
        const allowedRange = allowedRanges[currentAllowedIndex];
        if (allowedRange.endSeconds > sourceRange.startSeconds) {
          snappedRanges.push({
            startSeconds: allowedRange.startSeconds,
            endSeconds: allowedRange.endSeconds,
          });
        }

        if (allowedRange.endSeconds >= sourceRange.endSeconds) {
          break;
        }

        currentAllowedIndex += 1;
      }
    });

    return normalizeMergedRanges(snappedRanges);
  };

  const applyWaveformFilterToAiRanges = () => {
    aiRanges = isWaveformReady
      ? snapRangesToAcousticSilence(aiSourceRanges, acousticSilenceRanges)
      : [...aiSourceRanges];
  };

  const syncAiCutRangesInput = () => {
    aiCutRangesInput.value = JSON.stringify(aiRanges.map((range) => ({
      startSeconds: Number(range.startSeconds.toFixed(3)),
      endSeconds: Number(range.endSeconds.toFixed(3)),
    })));
  };

  const emitAiRangesChanged = ({ force = false } = {}) => {
    syncAiCutRangesInput();
    const nextKey = aiCutRangesInput.value;
    if (!force && nextKey === lastPublishedRangesKey) {
      return;
    }

    lastPublishedRangesKey = nextKey;
    form.dispatchEvent(new CustomEvent("strippr:ai-ranges-changed", {
      detail: {
        ranges: aiRanges.map((range) => ({
          startSeconds: range.startSeconds,
          endSeconds: range.endSeconds,
        })),
      },
    }));
  };

  const buildAnalysisStatusMessage = (serverMessage) => {
    if (!aiSourceRanges.length) {
      return serverMessage || "AI found no transcript gaps matching the current minimum silence.";
    }

    if (!isWaveformReady) {
      return serverMessage || "AI analysis completed.";
    }

    if (!aiRanges.length) {
      return "AI found transcript gaps, but none of them were actually quiet in the waveform.";
    }

    if (aiRanges.length === aiSourceRanges.length &&
        aiRanges.every((range, index) =>
          Math.abs(range.startSeconds - aiSourceRanges[index].startSeconds) < 0.001 &&
          Math.abs(range.endSeconds - aiSourceRanges[index].endSeconds) < 0.001)) {
      return serverMessage || "AI analysis completed.";
    }

    return `AI found ${aiSourceRanges.length} transcript gap ${aiSourceRanges.length === 1 ? "range" : "ranges"} and kept ${aiRanges.length} acoustically quiet ${aiRanges.length === 1 ? "range" : "ranges"}.`;
  };

  const renderAiOverlay = () => {
    if (!isAiEnabled() || !aiRanges.length || waveformPanel.hidden || !isWaveformReady) {
      aiOverlay.innerHTML = "";
      clearButton.hidden = !isAiEnabled() || aiRanges.length === 0;
      return;
    }

    const stageWidth = getStageWidth();
    const durationSeconds = getRenderDurationSeconds();
    if (stageWidth <= 0 || durationSeconds <= 0) {
      aiOverlay.innerHTML = "";
      clearButton.hidden = aiRanges.length === 0;
      return;
    }

    aiOverlay.innerHTML = "";
    aiRanges.forEach((range) => {
      const startX = secondsToX(range.startSeconds);
      const endX = secondsToX(range.endSeconds);
      const region = document.createElement("div");
      region.className = "waveform-ai-region";
      region.style.left = `${startX}px`;
      region.style.width = `${Math.max(2, endX - startX)}px`;
      region.title = `AI silence ${range.startSeconds.toFixed(2)}s - ${range.endSeconds.toFixed(2)}s`;
      aiOverlay.appendChild(region);
    });

    clearButton.hidden = false;
  };

  const clearAiRanges = ({ keepStatus = false } = {}) => {
    aiRanges = [];
    aiSourceRanges = [];
    analysisDurationSeconds = 0;
    aiOverlay.innerHTML = "";
    emitAiRangesChanged({ force: true });
    clearButton.hidden = true;
    if (!keepStatus) {
      setStatus("", { hidden: true });
    }
  };

  const syncAiBypassState = () => {
    syncToggleLabel();
    aiPanel.classList.toggle("is-bypassed", !isAiEnabled());
    apiKeyInput.disabled = !isAiEnabled();
    modelSelect.disabled = !isAiEnabled();
    clearButton.disabled = !isAiEnabled();

    if (!isAiEnabled()) {
      clearAiRanges();
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
    const hasOption = (value) => Array.from(modelSelect.options).some((option) => option.value === value);

    try {
      const storedModel = window.localStorage.getItem(modelStorageKey);
      if (storedModel && hasOption(storedModel)) {
        modelSelect.value = storedModel;
      } else if (defaultServerModel && hasOption(defaultServerModel)) {
        modelSelect.value = defaultServerModel;
      }

      const storedLanguage = window.localStorage.getItem(languageStorageKey);
      if (storedLanguage && Array.from(languageSelect.options).some((option) => option.value === storedLanguage)) {
        languageSelect.value = storedLanguage;
      }
    } catch {
      if (defaultServerModel && hasOption(defaultServerModel)) {
        modelSelect.value = defaultServerModel;
      }
    }
  };

  const analyzeWithAi = async () => {
    const file = fileInput.files?.[0];
    if (!file) {
      setStatus("Choose a video file before running AI analysis.", { isError: true });
      updateAnalyzeButtonState();
      return;
    }

    const apiKey = apiKeyInput.value.trim();
    if (!apiKey && !hasDefaultServerKey) {
      setStatus("Enter an OpenAI API key or add one to appsettings.Local.json before running AI analysis.", { isError: true });
      updateAnalyzeButtonState();
      return;
    }

    persistSettings();
    setAnalyzingState(true);
    setStatus("Analyzing transcript gaps with OpenAI...");

    const formData = new FormData();
    formData.append("video", file);
    formData.append("apiKey", apiKey);
    formData.append("model", modelSelect.value);
    formData.append("language", languageSelect.value);
    formData.append("minimumGapSeconds", silenceSlider instanceof HTMLInputElement ? silenceSlider.value : "0.5");

    try {
      const response = await fetch("/api/ai/silence-analysis", {
        method: "POST",
        body: formData,
      });

      const payload = await response.json().catch(() => null);
      if (!response.ok || !payload?.success) {
        throw new Error(payload?.message || "AI analysis failed.");
      }

      if (!isAiEnabled()) {
        clearAiRanges();
        return;
      }

      aiSourceRanges = normalizeRanges(payload.ranges);
      analysisDurationSeconds = Number(payload.durationSeconds) || 0;
      applyWaveformFilterToAiRanges();
      emitAiRangesChanged({ force: true });
      renderAiOverlay();
      setStatus(buildAnalysisStatusMessage(payload.message));
    } catch (error) {
      clearAiRanges({ keepStatus: true });
      setStatus(error instanceof Error ? error.message : "AI analysis failed.", { isError: true });
    } finally {
      setAnalyzingState(false);
    }
  };

  restoreSettings();
  syncAiBypassState();
  updateAnalyzeButtonState();
  clearButton.hidden = true;

  analyzeButton.addEventListener("click", () => {
    void analyzeWithAi();
  });

  clearButton.addEventListener("click", () => {
    clearAiRanges();
  });

  aiEnabledToggle.addEventListener("change", () => {
    syncAiBypassState();
  });

  apiKeyInput.addEventListener("input", () => {
    persistSettings();
    updateAnalyzeButtonState();
  });

  modelSelect.addEventListener("change", persistSettings);

  languageSelect.addEventListener("change", () => {
    try {
      window.localStorage.setItem(languageStorageKey, languageSelect.value);
    } catch {
    }
  });

  fileInput.addEventListener("change", () => {
    clearAiRanges();
    updateAnalyzeButtonState();
  });

  form.addEventListener("strippr:editor-reset", () => {
    aiEnabledToggle.checked = false;
    syncAiBypassState();
    clearAiRanges();
    updateAnalyzeButtonState();
  });

  form.addEventListener("strippr:waveform-state", (event) => {
    const detail = event.detail || {};
    waveformDurationSeconds = Number(detail.durationSeconds) || 0;
    acousticSilenceRanges = normalizeRanges(detail.acousticSilenceRanges);
    isWaveformReady = Boolean(detail.ready) && !Boolean(detail.hidden);
    applyWaveformFilterToAiRanges();
    emitAiRangesChanged();

    if (!isWaveformReady) {
      aiOverlay.innerHTML = "";
      clearButton.hidden = aiRanges.length === 0;
      return;
    }

    renderAiOverlay();
    updateAnalyzeButtonState();
  });

  if ("ResizeObserver" in window) {
    const resizeObserver = new ResizeObserver(() => {
      renderAiOverlay();
    });
    resizeObserver.observe(waveformStage);
  }
})();
