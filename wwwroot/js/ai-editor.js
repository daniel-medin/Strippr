(() => {
  const form = document.querySelector("[data-processing-form]");
  if (!form) {
    return;
  }

  const fileInput = form.querySelector("input[type='file']");
  const silenceSlider = form.querySelector("[data-silence-slider]");
  const apiKeyInput = form.querySelector("[data-ai-api-key]");
  const modelSelect = form.querySelector("[data-ai-model]");
  const analyzeButton = form.querySelector("[data-ai-analyze]");
  const analyzeLabel = form.querySelector("[data-ai-analyze-label]");
  const spinner = form.querySelector(".ai-analysis-spinner");
  const clearButton = form.querySelector("[data-ai-clear]");
  const status = form.querySelector("[data-ai-status]");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const aiOverlay = form.querySelector("[data-ai-waveform-overlay]");
  const aiPanel = form.querySelector(".ai-analysis-panel");

  if (
    !(fileInput instanceof HTMLInputElement) ||
    !(apiKeyInput instanceof HTMLInputElement) ||
    !(modelSelect instanceof HTMLSelectElement) ||
    !(analyzeButton instanceof HTMLButtonElement) ||
    !(analyzeLabel instanceof HTMLElement) ||
    !(clearButton instanceof HTMLButtonElement) ||
    !(status instanceof HTMLElement) ||
    !(waveformPanel instanceof HTMLElement) ||
    !(waveformStage instanceof HTMLElement) ||
    !(aiOverlay instanceof HTMLElement) ||
    !(aiPanel instanceof HTMLElement)
  ) {
    return;
  }

  const modelStorageKey = "strippr.ai.model";
  const hasDefaultServerKey = aiPanel.dataset.aiDefaultKeyAvailable === "true";
  const defaultServerModel = aiPanel.dataset.aiDefaultModel || "whisper-1";
  let aiRanges = [];
  let analysisDurationSeconds = 0;
  let waveformDurationSeconds = 0;
  let isWaveformReady = false;
  let isAnalyzing = false;

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

  const updateAnalyzeButtonState = () => {
    analyzeButton.disabled = isAnalyzing || !fileInput.files?.[0] || (!apiKeyInput.value.trim() && !hasDefaultServerKey);
  };

  const setAnalyzingState = (value) => {
    isAnalyzing = value;
    spinner.hidden = !value;
    analyzeLabel.textContent = value ? "Analyzing..." : "Analyze with AI";
    updateAnalyzeButtonState();
  };

  const renderAiOverlay = () => {
    if (!aiRanges.length || waveformPanel.hidden || !isWaveformReady) {
      aiOverlay.innerHTML = "";
      clearButton.hidden = aiRanges.length === 0;
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
    analysisDurationSeconds = 0;
    aiOverlay.innerHTML = "";
    clearButton.hidden = true;
    if (!keepStatus) {
      setStatus("", { hidden: true });
    }
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

      aiRanges = Array.isArray(payload.ranges)
        ? payload.ranges
            .map((range) => ({
              startSeconds: Number(range.startSeconds),
              endSeconds: Number(range.endSeconds),
            }))
            .filter((range) =>
              Number.isFinite(range.startSeconds) &&
              Number.isFinite(range.endSeconds) &&
              range.endSeconds > range.startSeconds)
        : [];
      analysisDurationSeconds = Number(payload.durationSeconds) || 0;

      renderAiOverlay();
      setStatus(payload.message || "AI analysis completed.");
    } catch (error) {
      clearAiRanges({ keepStatus: true });
      setStatus(error instanceof Error ? error.message : "AI analysis failed.", { isError: true });
    } finally {
      setAnalyzingState(false);
    }
  };

  restoreSettings();
  updateAnalyzeButtonState();
  clearButton.hidden = true;

  analyzeButton.addEventListener("click", () => {
    void analyzeWithAi();
  });

  clearButton.addEventListener("click", () => {
    clearAiRanges();
  });

  apiKeyInput.addEventListener("input", () => {
    persistSettings();
    updateAnalyzeButtonState();
  });

  modelSelect.addEventListener("change", persistSettings);

  fileInput.addEventListener("change", () => {
    clearAiRanges();
    updateAnalyzeButtonState();
  });

  form.addEventListener("strippr:waveform-state", (event) => {
    const detail = event.detail || {};
    waveformDurationSeconds = Number(detail.durationSeconds) || 0;
    isWaveformReady = Boolean(detail.ready) && !Boolean(detail.hidden);

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
