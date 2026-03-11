(() => {
  const form = document.querySelector("[data-processing-form]");

  if (!form) {
    return;
  }

  const processingPanel = form.querySelector("[data-processing-panel]");
  const processingFill = form.querySelector("[data-processing-fill]");
  const processingLabel = form.querySelector("[data-processing-label]");
  const processingPercent = form.querySelector("[data-processing-percent]");
  const processingBar = form.querySelector("[data-processing-bar]");
  const fileInput = form.querySelector("input[type='file']");
  const pickFileButton = form.querySelector("[data-pick-file]");
  const fileNameDisplay = form.querySelector("[data-file-name]");
  const noiseInput = form.querySelector("input[name='Input.NoiseThreshold']");
  const noiseSlider = form.querySelector("[data-noise-slider]");
  const noiseDisplay = form.querySelector("[data-noise-display]");
  const silenceSlider = form.querySelector("[data-silence-slider]");
  const silenceDisplay = form.querySelector("[data-silence-display]");
  const retainedSilenceSlider = form.querySelector("[data-retained-silence-slider]");
  const retainedSilenceDisplay = form.querySelector("[data-retained-silence-display]");
  const cutHandleSlider = form.querySelector("[data-cut-handle-slider]");
  const cutHandleDisplay = form.querySelector("[data-cut-handle-display]");
  const crossfadeSlider = form.querySelector("[data-crossfade-slider]");
  const crossfadeDisplay = form.querySelector("[data-crossfade-display]");
  const videoCrossfadeSlider = form.querySelector("[data-video-crossfade-slider]");
  const videoCrossfadeDisplay = form.querySelector("[data-video-crossfade-display]");
  const pauseSpeedSlider = form.querySelector("[data-pause-speed-slider]");
  const pauseSpeedDisplay = form.querySelector("[data-pause-speed-display]");
  const validationSummary = form.querySelector(".validation-summary");
  let resultPanel = document.querySelector("[data-result-panel]");
  const submitButton = form.querySelector("button[type='submit']");
  const waveformPanel = form.querySelector("[data-waveform-panel]");
  const waveformViewport = form.querySelector("[data-waveform-viewport]");
  const waveformStage = form.querySelector("[data-waveform-stage]");
  const waveformCanvas = form.querySelector("[data-waveform-canvas]");
  const waveformOverlay = form.querySelector("[data-waveform-overlay]");
  const waveformStatus = form.querySelector("[data-waveform-status]");
  const thresholdReadout = form.querySelector("[data-threshold-readout]");
  const waveformVerticalZoom = form.querySelector("[data-waveform-vertical-zoom]");
  const waveformVerticalDisplay = form.querySelector("[data-waveform-vertical-display]");
  const waveformHorizontalZoom = form.querySelector("[data-waveform-horizontal-zoom]");
  const waveformHorizontalDisplay = form.querySelector("[data-waveform-horizontal-display]");
  const playAudioButton = form.querySelector("[data-waveform-play]");
  const playResultButton = form.querySelector("[data-waveform-play-result]");
  const manualCutRangesInput = form.querySelector("[data-manual-cut-ranges]");
  const markerSummary = form.querySelector("[data-marker-summary]");
  const markerList = form.querySelector("[data-marker-list]");
  const addMarkerButtons = Array.from(form.querySelectorAll("[data-add-marker]"));
  const resetAllButton = form.querySelector("[data-reset-all]");

  if (!processingPanel || !processingFill || !processingLabel || !processingPercent || !processingBar || !fileInput || !submitButton) {
    return;
  }

  let progressValue = 0;
  let processingTimerId = null;
  let waveformPeaks = null;
  let waveformDurationSeconds = 0;
  let waveformToken = 0;
  let verticalZoomValue = 1;
  let horizontalZoomValue = 1;
  let playheadSeconds = null;
  let markerSequence = 0;
  let selectedMarkerId = null;
  let dragMarkerId = null;
  let manualMarkers = [];
  let decodedAudioBuffer = null;
  let previewAudio = null;
  let previewAudioUrl = null;
  let playbackFrameId = null;
  let resultPreviewContext = null;
  let resultPreviewSources = [];
  let resultPreviewEntries = [];
  let resultPreviewBaseTime = 0;
  let resultPlaybackFrameId = null;
  let detectedSilenceCacheKey = "";
  let detectedSilenceCache = [];
  let defaultState = null;

  const minimumKeepSegmentSeconds = 0.15;

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  const getWaveformWidth = () => waveformCanvas ? waveformCanvas.clientWidth || Number.parseFloat(waveformCanvas.style.width) || 0 : 0;
  const secondsToX = (seconds) => waveformDurationSeconds > 0 ? clamp(seconds / waveformDurationSeconds, 0, 1) * getWaveformWidth() : 0;
  const xToSeconds = (x) => {
    const width = getWaveformWidth();
    return width > 0 && waveformDurationSeconds > 0 ? clamp(x / width, 0, 1) * waveformDurationSeconds : 0;
  };

  const sortMarkers = () =>
    [...manualMarkers].sort((a, b) =>
      a.timeSeconds !== b.timeSeconds
        ? a.timeSeconds - b.timeSeconds
        : a.type === b.type
          ? a.id.localeCompare(b.id)
          : a.type === "in"
            ? -1
            : 1,
    );

  const formatDuration = (seconds, includeTenths = false) => {
    const safeSeconds = Math.max(0, seconds);
    const wholeSeconds = Math.floor(safeSeconds);
    const hours = Math.floor(wholeSeconds / 3600).toString().padStart(2, "0");
    const minutes = Math.floor((wholeSeconds % 3600) / 60).toString().padStart(2, "0");
    const remainingSeconds = (wholeSeconds % 60).toString().padStart(2, "0");

    if (!includeTenths) {
      return `${hours}:${minutes}:${remainingSeconds}`;
    }

    return `${hours}:${minutes}:${remainingSeconds}.${Math.floor((safeSeconds - wholeSeconds) * 10)}`;
  };

  const parseThresholdDb = () => {
    if (!noiseInput) {
      return -30;
    }

    const match = `${noiseInput.value}`.trim().match(/(-?\d+(?:\.\d+)?)\s*dB/i);
    return match ? Number.parseFloat(match[1]) : null;
  };

  const setProgress = (value, text) => {
    progressValue = clamp(value, 0, 100);
    processingFill.style.width = `${progressValue}%`;
    processingPercent.textContent = `${Math.round(progressValue)}%`;
    processingBar.setAttribute("aria-valuenow", `${Math.round(progressValue)}`);

    if (text) {
      processingLabel.textContent = text;
    }
  };

  const clearProcessingTimer = () => {
    if (processingTimerId) {
      window.clearInterval(processingTimerId);
      processingTimerId = null;
    }
  };

  const beginProcessingSimulation = () => {
    clearProcessingTimer();
    processingTimerId = window.setInterval(() => {
      if (progressValue < 54) {
        setProgress(progressValue + 2.5, "Analyzing silence...");
      } else if (progressValue < 84) {
        setProgress(progressValue + 1.6, "Rendering cleaned video...");
      } else if (progressValue < 96) {
        setProgress(progressValue + 0.45, "Finalizing output...");
      }
    }, 350);
  };

  const showFailure = (message) => {
    clearProcessingTimer();
    submitButton.disabled = false;
    form.classList.remove("is-processing");
    processingPanel.hidden = true;
    processingLabel.textContent = "Processing failed.";
    processingPercent.textContent = `${Math.round(progressValue)}%`;

    if (validationSummary) {
      validationSummary.textContent = message;
    }
  };

  const finishProcessing = () => {
    clearProcessingTimer();
    submitButton.disabled = false;
    form.classList.remove("is-processing");
    processingPanel.hidden = true;
  };

  const captureDefaultState = () => ({
    noiseSlider: noiseSlider ? noiseSlider.value : null,
    silenceSlider: silenceSlider ? silenceSlider.value : null,
    retainedSilenceSlider: retainedSilenceSlider ? retainedSilenceSlider.value : null,
    cutHandleSlider: cutHandleSlider ? cutHandleSlider.value : null,
    crossfadeSlider: crossfadeSlider ? crossfadeSlider.value : null,
    videoCrossfadeSlider: videoCrossfadeSlider ? videoCrossfadeSlider.value : null,
    pauseSpeedSlider: pauseSpeedSlider ? pauseSpeedSlider.value : null,
    waveformVerticalZoom: waveformVerticalZoom ? waveformVerticalZoom.value : "1",
    waveformHorizontalZoom: waveformHorizontalZoom ? waveformHorizontalZoom.value : "1",
  });

  const clearWaveformCanvas = () => {
    if (waveformCanvas) {
      const context = waveformCanvas.getContext("2d");
      if (context) {
        context.clearRect(0, 0, waveformCanvas.width, waveformCanvas.height);
      }

      waveformCanvas.width = 0;
      waveformCanvas.height = 0;
      waveformCanvas.style.width = "";
      waveformCanvas.style.height = "";
    }

    if (waveformStage) {
      waveformStage.style.width = "";
      waveformStage.style.height = "";
    }

    if (waveformOverlay) {
      waveformOverlay.innerHTML = "";
    }
  };

  const syncSelectedFileName = () => {
    if (!fileNameDisplay) {
      return;
    }

    const file = fileInput && fileInput.files && fileInput.files[0];
    fileNameDisplay.textContent = file ? file.name : "No file selected";
  };

  const applyServerResponse = (responseText) => {
    const parser = new DOMParser();
    const responseDocument = parser.parseFromString(responseText, "text/html");
    const nextResultPanel = responseDocument.querySelector("[data-result-panel]");
    if (nextResultPanel && resultPanel) {
      resultPanel.replaceWith(nextResultPanel);
      resultPanel = nextResultPanel;
    }

    const nextValidationSummary = responseDocument.querySelector(".validation-summary");
    if (validationSummary) {
      validationSummary.innerHTML = nextValidationSummary ? nextValidationSummary.innerHTML : "";
    }
  };

  const updateThresholdReadout = () => {
    if (!thresholdReadout) {
      return;
    }

    const thresholdDb = parseThresholdDb();
    thresholdReadout.textContent = thresholdDb === null ? "Invalid dB" : `${thresholdDb.toFixed(1)} dB`;
  };

  const getSliderRatio = (slider) => {
    if (!(slider instanceof HTMLInputElement)) {
      return 0;
    }

    const min = Number.parseFloat(slider.min || "0");
    const max = Number.parseFloat(slider.max || "100");
    const value = Number.parseFloat(slider.value);

    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min || !Number.isFinite(value)) {
      return 0;
    }

    return clamp((value - min) / (max - min), 0, 1);
  };

  const syncSliderVisual = (slider, display) => {
    const ratio = getSliderRatio(slider);

    if (slider instanceof HTMLInputElement) {
      slider.style.setProperty("--range-percent", `${(ratio * 100).toFixed(2)}%`);
      slider.style.setProperty("--min", slider.min || "0");
      slider.style.setProperty("--max", slider.max || "100");
      slider.style.setProperty("--value", slider.value || "0");

      const rulerSlider = slider.closest(".ruler-slider");
      if (rulerSlider instanceof HTMLElement) {
        rulerSlider.style.setProperty("--min", slider.min || "0");
        rulerSlider.style.setProperty("--max", slider.max || "100");
        rulerSlider.style.setProperty("--val", slider.value || "0");
      }
    }

    if (display instanceof HTMLElement) {
      display.style.setProperty("--value-ratio", ratio.toFixed(4));
    }
  };

  const syncNoiseControls = () => {
    if (!noiseInput || !noiseSlider) {
      return;
    }

    const sliderValue = Number.parseFloat(noiseSlider.value);
    noiseInput.value = `${sliderValue.toFixed(0)}dB`;

    if (noiseDisplay) {
      noiseDisplay.textContent = `${sliderValue.toFixed(0)} dB`;
    }

    syncSliderVisual(noiseSlider, noiseDisplay);
  };

  const syncSilenceControls = () => {
    if (!silenceSlider) {
      return;
    }

    const sliderValue = Number.parseFloat(silenceSlider.value);
    if (silenceDisplay) {
      silenceDisplay.textContent = `${sliderValue.toFixed(1)} s`;
    }

    syncSliderVisual(silenceSlider, silenceDisplay);
  };

  const syncRetainedSilenceControls = () => {
    if (!retainedSilenceSlider || !retainedSilenceDisplay) {
      return;
    }

    const sliderValue = Number.parseFloat(retainedSilenceSlider.value);
    retainedSilenceDisplay.textContent = `${sliderValue.toFixed(2)} s`;
    syncSliderVisual(retainedSilenceSlider, retainedSilenceDisplay);
  };

  const syncCutHandleControls = () => {
    if (!cutHandleSlider || !cutHandleDisplay) {
      return;
    }

    const sliderValue = Number.parseFloat(cutHandleSlider.value);
    cutHandleDisplay.textContent = `${sliderValue.toFixed(0)} ms`;
    syncSliderVisual(cutHandleSlider, cutHandleDisplay);
  };

  const syncCrossfadeControls = () => {
    if (!crossfadeSlider || !crossfadeDisplay) {
      return;
    }

    const sliderValue = Number.parseFloat(crossfadeSlider.value);
    crossfadeDisplay.textContent = `${sliderValue.toFixed(0)} ms`;
    syncSliderVisual(crossfadeSlider, crossfadeDisplay);
  };

  const syncVideoCrossfadeControls = () => {
    if (!videoCrossfadeSlider || !videoCrossfadeDisplay) {
      return;
    }

    const sliderValue = Number.parseInt(videoCrossfadeSlider.value, 10);
    videoCrossfadeDisplay.textContent = `${sliderValue} fr`;
    syncSliderVisual(videoCrossfadeSlider, videoCrossfadeDisplay);
  };

  const syncPauseSpeedControls = () => {
    if (!pauseSpeedSlider || !pauseSpeedDisplay) {
      return;
    }

    const sliderValue = Number.parseFloat(pauseSpeedSlider.value);
    pauseSpeedDisplay.textContent = `${sliderValue.toFixed(1)}x`;
    syncSliderVisual(pauseSpeedSlider, pauseSpeedDisplay);
  };

  const syncWaveformZoomControls = () => {
    if (waveformVerticalZoom) {
      verticalZoomValue = Number.parseFloat(waveformVerticalZoom.value);
      if (waveformVerticalDisplay) {
        waveformVerticalDisplay.textContent = `${verticalZoomValue.toFixed(1)}x`;
      }

      syncSliderVisual(waveformVerticalZoom, waveformVerticalDisplay);
    }

    if (waveformHorizontalZoom) {
      horizontalZoomValue = Number.parseFloat(waveformHorizontalZoom.value);
      if (waveformHorizontalDisplay) {
        waveformHorizontalDisplay.textContent = `${horizontalZoomValue.toFixed(1)}x`;
      }

      syncSliderVisual(waveformHorizontalZoom, waveformHorizontalDisplay);
    }
  };

  const stopPlaybackTracking = () => {
    if (playbackFrameId) {
      window.cancelAnimationFrame(playbackFrameId);
      playbackFrameId = null;
    }
  };

  const stopResultPlaybackTracking = () => {
    if (resultPlaybackFrameId) {
      window.cancelAnimationFrame(resultPlaybackFrameId);
      resultPlaybackFrameId = null;
    }
  };

  const syncPlaybackButtons = () => {
    if (playAudioButton) {
      playAudioButton.disabled = !previewAudio;
      playAudioButton.textContent = previewAudio && !previewAudio.paused ? "Pause audio (Space)" : "Play audio (Space)";
    }

    if (playResultButton) {
      playResultButton.disabled = !decodedAudioBuffer;
      playResultButton.textContent = resultPreviewContext ? "Pause result (R)" : "Play result (R)";
    }
  };

  const trackPlayback = () => {
    if (!previewAudio || previewAudio.paused) {
      stopPlaybackTracking();
      syncPlaybackButtons();
      renderWaveformOverlay();
      return;
    }

    playheadSeconds = previewAudio.currentTime;
    renderWaveformOverlay();
    playbackFrameId = window.requestAnimationFrame(trackPlayback);
  };

  const stopPreviewAudio = ({ resetTime = false } = {}) => {
    if (!previewAudio) {
      stopPlaybackTracking();
      syncPlaybackButtons();
      return;
    }

    previewAudio.pause();
    if (resetTime) {
      previewAudio.currentTime = 0;
      playheadSeconds = 0;
    } else {
      playheadSeconds = previewAudio.currentTime;
    }

    stopPlaybackTracking();
    syncPlaybackButtons();
    renderWaveformOverlay();
  };

  const findSourceTimeForResultElapsed = (elapsedSeconds) => {
    if (resultPreviewEntries.length === 0) {
      return playheadSeconds ?? 0;
    }

    let activeEntry = resultPreviewEntries[0];
    for (const entry of resultPreviewEntries) {
      if (elapsedSeconds >= entry.resultStartSeconds) {
        activeEntry = entry;
      } else {
        break;
      }
    }

    return clamp(
      activeEntry.sourceStartSeconds + Math.max(0, elapsedSeconds - activeEntry.resultStartSeconds) * activeEntry.playbackSpeed,
      activeEntry.sourceStartSeconds,
      activeEntry.sourceEndSeconds,
    );
  };

  const trackResultPlayback = () => {
    if (!resultPreviewContext) {
      stopResultPlaybackTracking();
      syncPlaybackButtons();
      renderWaveformOverlay();
      return;
    }

    const elapsedSeconds = Math.max(0, resultPreviewContext.currentTime - resultPreviewBaseTime);
    playheadSeconds = findSourceTimeForResultElapsed(elapsedSeconds);
    renderWaveformOverlay();

    const lastEntry = resultPreviewEntries[resultPreviewEntries.length - 1];
    if (!lastEntry || elapsedSeconds >= lastEntry.resultEndSeconds) {
      stopResultPreview();
      return;
    }

    resultPlaybackFrameId = window.requestAnimationFrame(trackResultPlayback);
  };

  const stopResultPreview = () => {
    resultPreviewSources.forEach((source) => {
      try {
        source.stop();
      } catch {
      }
    });

    resultPreviewSources = [];
    resultPreviewEntries = [];
    resultPreviewBaseTime = 0;
    stopResultPlaybackTracking();

    if (resultPreviewContext) {
      resultPreviewContext.close().catch(() => {});
      resultPreviewContext = null;
    }

    syncPlaybackButtons();
    renderWaveformOverlay();
  };

  const disposePreviewAudio = () => {
    stopPreviewAudio({ resetTime: true });
    stopResultPreview();
    decodedAudioBuffer = null;

    if (previewAudio) {
      previewAudio.src = "";
      previewAudio.load();
      previewAudio = null;
    }

    if (previewAudioUrl) {
      URL.revokeObjectURL(previewAudioUrl);
      previewAudioUrl = null;
    }

    syncPlaybackButtons();
  };

  const getPreviewDuration = () => {
    if (waveformDurationSeconds > 0) {
      return waveformDurationSeconds;
    }

    if (previewAudio && Number.isFinite(previewAudio.duration) && previewAudio.duration > 0) {
      return previewAudio.duration;
    }

    return 0;
  };

  const getMinimumSilenceSeconds = () => {
    if (!silenceSlider) {
      return 0.5;
    }

    return Number.parseFloat(silenceSlider.value);
  };

  const getCrossfadeMilliseconds = () => {
    if (!crossfadeSlider) {
      return 0;
    }

    return Number.parseFloat(crossfadeSlider.value);
  };

  const getRetainedSilenceSeconds = () => {
    if (!retainedSilenceSlider) {
      return 0;
    }

    return Math.max(0, Number.parseFloat(retainedSilenceSlider.value) || 0);
  };

  const getCutHandleSeconds = () => {
    if (!cutHandleSlider) {
      return 0;
    }

    return Math.max(0, Number.parseFloat(cutHandleSlider.value) || 0) / 1000;
  };

  const getPauseSpeedMultiplier = () => {
    if (!pauseSpeedSlider) {
      return 1;
    }

    return Math.max(1, Number.parseFloat(pauseSpeedSlider.value) || 1);
  };

  const initializePreviewAudio = (file) => {
    disposePreviewAudio();

    previewAudioUrl = URL.createObjectURL(file);
    previewAudio = new Audio(previewAudioUrl);
    previewAudio.preload = "auto";
    previewAudio.addEventListener("pause", () => {
      stopPlaybackTracking();
      syncPlaybackButtons();
      renderWaveformOverlay();
    });
    previewAudio.addEventListener("ended", () => {
      stopPreviewAudio();
    });
    previewAudio.addEventListener("play", () => {
      syncPlaybackButtons();
      stopPlaybackTracking();
      trackPlayback();
    });
    syncPlaybackButtons();
  };

  const setMarkerButtonsEnabled = (isEnabled) => {
    addMarkerButtons.forEach((button) => {
      button.disabled = !isEnabled;
    });
  };

  const normalizeRemovedRanges = (durationSeconds, ranges) => {
    const orderedRanges = ranges
      .map((range) => ({
        startSeconds: clamp(range.startSeconds, 0, durationSeconds),
        endSeconds: clamp(range.endSeconds, 0, durationSeconds),
      }))
      .filter((range) => range.endSeconds > range.startSeconds)
      .sort((left, right) => left.startSeconds - right.startSeconds);

    if (orderedRanges.length === 0) {
      return [];
    }

    const merged = [orderedRanges[0]];
    for (let index = 1; index < orderedRanges.length; index += 1) {
      const current = orderedRanges[index];
      const previous = merged[merged.length - 1];
      if (current.startSeconds <= previous.endSeconds) {
        previous.endSeconds = Math.max(previous.endSeconds, current.endSeconds);
      } else {
        merged.push(current);
      }
    }

    return merged;
  };

  const buildKeepSegments = (durationSeconds, removedRanges) => {
    const keepSegments = [];
    let cursor = 0;

    removedRanges.forEach((range) => {
      if (range.startSeconds - cursor >= minimumKeepSegmentSeconds) {
        keepSegments.push({ startSeconds: cursor, endSeconds: range.startSeconds });
      }

      cursor = range.endSeconds;
    });

    if (durationSeconds - cursor >= minimumKeepSegmentSeconds) {
      keepSegments.push({ startSeconds: cursor, endSeconds: durationSeconds });
    }

    return keepSegments;
  };

  const subtractRanges = (sourceRanges, excludedRanges) => {
    if (sourceRanges.length === 0) {
      return [];
    }

    if (excludedRanges.length === 0) {
      return sourceRanges.map((range) => ({ ...range }));
    }

    const result = [];
    let excludedIndex = 0;

    sourceRanges.forEach((sourceRange) => {
      let cursor = sourceRange.startSeconds;

      while (excludedIndex < excludedRanges.length && excludedRanges[excludedIndex].endSeconds <= sourceRange.startSeconds) {
        excludedIndex += 1;
      }

      let currentExcludedIndex = excludedIndex;
      while (currentExcludedIndex < excludedRanges.length && excludedRanges[currentExcludedIndex].startSeconds < sourceRange.endSeconds) {
        const excludedRange = excludedRanges[currentExcludedIndex];
        if (excludedRange.startSeconds > cursor) {
          result.push({
            startSeconds: cursor,
            endSeconds: Math.min(excludedRange.startSeconds, sourceRange.endSeconds),
          });
        }

        cursor = Math.max(cursor, excludedRange.endSeconds);
        if (cursor >= sourceRange.endSeconds) {
          break;
        }

        currentExcludedIndex += 1;
      }

      if (cursor < sourceRange.endSeconds) {
        result.push({
          startSeconds: cursor,
          endSeconds: sourceRange.endSeconds,
        });
      }
    });

    return result;
  };

  const detectSilenceRanges = () => {
    if (!decodedAudioBuffer) {
      return [];
    }

    const thresholdDb = parseThresholdDb();
    const minimumSilenceSeconds = getMinimumSilenceSeconds();
    const cacheKey = `${waveformToken}|${thresholdDb ?? "invalid"}|${minimumSilenceSeconds.toFixed(3)}`;
    if (detectedSilenceCacheKey === cacheKey) {
      return detectedSilenceCache;
    }

    const sampleRate = decodedAudioBuffer.sampleRate;
    const frameSize = Math.max(1, Math.floor(sampleRate * 0.01));
    const channelCount = decodedAudioBuffer.numberOfChannels;
    const channels = [];
    for (let index = 0; index < channelCount; index += 1) {
      channels.push(decodedAudioBuffer.getChannelData(index));
    }

    const thresholdAmplitude = thresholdDb === null ? 0 : Math.max(0, Math.min(1, Math.pow(10, thresholdDb / 20)));
    const detectedRanges = [];
    let silenceStartSeconds = null;

    for (let sampleIndex = 0; sampleIndex < decodedAudioBuffer.length; sampleIndex += frameSize) {
      const frameEnd = Math.min(sampleIndex + frameSize, decodedAudioBuffer.length);
      let peakAmplitude = 0;

      for (let innerIndex = sampleIndex; innerIndex < frameEnd; innerIndex += 1) {
        let mixedSample = 0;
        for (let channelIndex = 0; channelIndex < channelCount; channelIndex += 1) {
          mixedSample += channels[channelIndex][innerIndex] || 0;
        }

        mixedSample /= channelCount;
        peakAmplitude = Math.max(peakAmplitude, Math.abs(mixedSample));
      }

      const currentFrameSeconds = sampleIndex / sampleRate;
      const isSilent = peakAmplitude <= thresholdAmplitude;

      if (isSilent && silenceStartSeconds === null) {
        silenceStartSeconds = currentFrameSeconds;
      }

      if (!isSilent && silenceStartSeconds !== null) {
        const endSeconds = currentFrameSeconds;
        if (endSeconds - silenceStartSeconds >= minimumSilenceSeconds) {
          detectedRanges.push({ startSeconds: silenceStartSeconds, endSeconds });
        }

        silenceStartSeconds = null;
      }
    }

    if (silenceStartSeconds !== null) {
      const endSeconds = decodedAudioBuffer.duration;
      if (endSeconds - silenceStartSeconds >= minimumSilenceSeconds) {
        detectedRanges.push({ startSeconds: silenceStartSeconds, endSeconds });
      }
    }

    detectedSilenceCacheKey = cacheKey;
    detectedSilenceCache = detectedRanges;
    return detectedRanges;
  };

  const determineAppliedCrossfadeSeconds = (segments, requestedCrossfadeSeconds) => {
    if (requestedCrossfadeSeconds <= 0 || segments.length < 2) {
      return 0;
    }

    let maxAllowed = Number.POSITIVE_INFINITY;
    let foundHardCut = false;
    for (let index = 1; index < segments.length; index += 1) {
      if (!segments[index].startsAfterHardCut) {
        continue;
      }

      foundHardCut = true;
      const currentDuration = segments[index - 1].outputDurationSeconds;
      const nextDuration = segments[index].outputDurationSeconds;
      maxAllowed = Math.min(maxAllowed, Math.max(0, Math.min(currentDuration, nextDuration) - 0.01));
    }

    if (!foundHardCut || !Number.isFinite(maxAllowed) || maxAllowed <= 0) {
      return 0;
    }

    return Math.min(requestedCrossfadeSeconds, maxAllowed);
  };

  const calculateSilencePlaybackSpeed = (durationSeconds, pauseSpeedMultiplier, retainedSilenceSeconds) => {
    if (durationSeconds <= 0) {
      return 1;
    }

    let targetDurationSeconds;
    if (retainedSilenceSeconds > 0 && pauseSpeedMultiplier <= 1) {
      targetDurationSeconds = Math.min(durationSeconds, retainedSilenceSeconds);
    } else {
      targetDurationSeconds = durationSeconds / Math.max(1, pauseSpeedMultiplier);
      if (retainedSilenceSeconds > 0) {
        targetDurationSeconds = Math.max(retainedSilenceSeconds, targetDurationSeconds);
      }
    }

    targetDurationSeconds = clamp(targetDurationSeconds, 0, durationSeconds);
    return targetDurationSeconds <= 0 ? 1 : Math.max(1, durationSeconds / targetDurationSeconds);
  };

  const applyCutHandles = (ranges, handleSeconds) => {
    if (ranges.length === 0 || handleSeconds <= 0) {
      return ranges.map((range) => ({ ...range }));
    }

    return ranges
      .map((range) => ({
        startSeconds: range.startSeconds + handleSeconds,
        endSeconds: range.endSeconds - handleSeconds,
      }))
      .filter((range) => range.endSeconds > range.startSeconds);
  };

  const buildResultPreviewSegments = () => {
    if (!decodedAudioBuffer) {
      return [];
    }

    const durationSeconds = decodedAudioBuffer.duration;
    const pauseSpeedMultiplier = getPauseSpeedMultiplier();
    const retainedSilenceSeconds = getRetainedSilenceSeconds();
    const cutHandleSeconds = getCutHandleSeconds();
    const manualRanges = normalizeRemovedRanges(durationSeconds, buildManualCutRanges().ranges);
    const silenceRanges = normalizeRemovedRanges(
      durationSeconds,
      applyCutHandles(detectSilenceRanges(), cutHandleSeconds),
    );

    if (pauseSpeedMultiplier <= 1 && retainedSilenceSeconds <= 0) {
      const removedRanges = normalizeRemovedRanges(durationSeconds, silenceRanges.concat(manualRanges));
      return buildKeepSegments(durationSeconds, removedRanges).map((segment, index) => ({
        ...segment,
        playbackSpeed: 1,
        startsAfterHardCut: index > 0,
        outputDurationSeconds: segment.endSeconds - segment.startSeconds,
      }));
    }

    const compressibleSilences = subtractRanges(silenceRanges, manualRanges);
    const events = manualRanges
      .map((range) => ({ ...range, isHardCut: true }))
      .concat(compressibleSilences.map((range) => ({ ...range, isHardCut: false })))
      .sort((left, right) => left.startSeconds - right.startSeconds || left.endSeconds - right.endSeconds);
    const segments = [];
    let cursor = 0;
    let nextSegmentStartsAfterHardCut = false;

    const pushSegment = (startSeconds, endSeconds, playbackSpeed, applyMinimumKeepThreshold) => {
      const durationSecondsForSegment = endSeconds - startSeconds;
      if (durationSecondsForSegment <= 0) {
        return;
      }

      if (applyMinimumKeepThreshold && durationSecondsForSegment < minimumKeepSegmentSeconds) {
        return;
      }

      segments.push({
        startSeconds,
        endSeconds,
        playbackSpeed,
        startsAfterHardCut: nextSegmentStartsAfterHardCut && segments.length > 0,
        outputDurationSeconds: durationSecondsForSegment / playbackSpeed,
      });
      nextSegmentStartsAfterHardCut = false;
    };

    events.forEach((event) => {
      pushSegment(cursor, event.startSeconds, 1, true);

      if (event.isHardCut) {
        cursor = event.endSeconds;
        nextSegmentStartsAfterHardCut = true;
        return;
      }

      pushSegment(
        event.startSeconds,
        event.endSeconds,
        calculateSilencePlaybackSpeed(event.endSeconds - event.startSeconds, pauseSpeedMultiplier, retainedSilenceSeconds),
        false,
      );
      cursor = event.endSeconds;
    });

    pushSegment(cursor, durationSeconds, 1, true);
    return segments;
  };

  const buildManualCutRanges = () => {
    const orderedMarkers = sortMarkers();
    const ranges = [];
    let openInMarker = null;

    orderedMarkers.forEach((marker) => {
      if (marker.type === "in") {
        openInMarker = marker;
        return;
      }

      if (!openInMarker || marker.timeSeconds <= openInMarker.timeSeconds) {
        return;
      }

      ranges.push({ startSeconds: openInMarker.timeSeconds, endSeconds: marker.timeSeconds });
      openInMarker = null;
    });

    return {
      orderedMarkers,
      ranges,
      unpairedCount: Math.max(0, orderedMarkers.length - ranges.length * 2),
    };
  };

  const buildAutomaticEditRanges = (manualRanges) => {
    if (!decodedAudioBuffer) {
      return [];
    }

    const durationSeconds = decodedAudioBuffer.duration;
    const pauseSpeedMultiplier = getPauseSpeedMultiplier();
    const retainedSilenceSeconds = getRetainedSilenceSeconds();
    const cutHandleSeconds = getCutHandleSeconds();
    const silenceRanges = normalizeRemovedRanges(
      durationSeconds,
      applyCutHandles(detectSilenceRanges(), cutHandleSeconds),
    );
    const automaticRanges = subtractRanges(silenceRanges, manualRanges);
    const isHardCut = pauseSpeedMultiplier <= 1 && retainedSilenceSeconds <= 0;

    return automaticRanges.map((range) => ({
      ...range,
      isHardCut,
    }));
  };

  const renderMarkerList = (orderedMarkers) => {
    if (!markerList) {
      return;
    }

    if (orderedMarkers.length === 0) {
      markerList.hidden = true;
      markerList.innerHTML = "";
      return;
    }

    markerList.hidden = false;
    markerList.innerHTML = orderedMarkers.map((marker) => `
      <div class="marker-chip${marker.id === selectedMarkerId ? " is-selected" : ""}">
        <button type="button" class="marker-chip-main" data-marker-focus="${marker.id}">
          <span class="marker-chip-type">${marker.type === "in" ? "In" : "Out"}</span>
          <span>${formatDuration(marker.timeSeconds, true)}</span>
        </button>
        <button type="button" class="marker-chip-delete" data-marker-remove="${marker.id}" aria-label="Delete marker">
          Remove
        </button>
      </div>
    `).join("");
  };

  const updateMarkerSummary = (ranges, unpairedCount) => {
    if (!markerSummary) {
      return;
    }

    if (manualMarkers.length === 0) {
      markerSummary.hidden = true;
      markerSummary.textContent = "";
      return;
    }

    markerSummary.hidden = false;
    markerSummary.textContent = `${ranges.length} cut ${ranges.length === 1 ? "range" : "ranges"} ready; ${
      unpairedCount > 0
        ? `${unpairedCount} marker${unpairedCount === 1 ? "" : "s"} waiting for a pair`
        : "all markers are paired"
    }.`;
  };

  const renderWaveformOverlay = () => {
    if (!waveformOverlay || waveformDurationSeconds <= 0) {
      if (waveformOverlay) {
        waveformOverlay.innerHTML = "";
      }

      return;
    }

    waveformOverlay.innerHTML = "";
    const { orderedMarkers, ranges } = buildManualCutRanges();
    const automaticRanges = buildAutomaticEditRanges(ranges);

    automaticRanges.forEach((range) => {
      const region = document.createElement("div");
      region.className = `waveform-auto-region ${range.isHardCut ? "is-hard-cut" : "is-compressed"}`;
      region.style.left = `${secondsToX(range.startSeconds)}px`;
      region.style.width = `${Math.max(2, secondsToX(range.endSeconds) - secondsToX(range.startSeconds))}px`;
      waveformOverlay.appendChild(region);
    });

    ranges.forEach((range) => {
      const region = document.createElement("div");
      region.className = "waveform-cut-region";
      region.style.left = `${secondsToX(range.startSeconds)}px`;
      region.style.width = `${Math.max(2, secondsToX(range.endSeconds) - secondsToX(range.startSeconds))}px`;
      waveformOverlay.appendChild(region);
    });

    if (playheadSeconds !== null) {
      const playhead = document.createElement("div");
      playhead.className = "waveform-playhead";
      playhead.style.left = `${secondsToX(playheadSeconds)}px`;
      waveformOverlay.appendChild(playhead);
    }

    orderedMarkers.forEach((marker) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `waveform-marker-button ${marker.type === "in" ? "is-in" : "is-out"}${marker.id === selectedMarkerId ? " is-selected" : ""}`;
      button.setAttribute("data-marker-id", marker.id);
      button.setAttribute("aria-label", `${marker.type === "in" ? "In" : "Out"} marker at ${formatDuration(marker.timeSeconds, true)}`);
      button.style.left = `${secondsToX(marker.timeSeconds)}px`;
      button.innerHTML = '<span class="waveform-marker-line"></span><span class="waveform-marker-flag"></span>';
      waveformOverlay.appendChild(button);
    });
  };

  const syncManualCutRangesInput = () => {
    stopResultPreview();
    const { orderedMarkers, ranges, unpairedCount } = buildManualCutRanges();
    if (manualCutRangesInput) {
      manualCutRangesInput.value = JSON.stringify(ranges.map((range) => ({
        startSeconds: Number(range.startSeconds.toFixed(3)),
        endSeconds: Number(range.endSeconds.toFixed(3)),
      })));
    }

    updateMarkerSummary(ranges, unpairedCount);
    renderMarkerList(orderedMarkers);
    renderWaveformOverlay();
  };

  const resetManualCuts = () => {
    stopResultPreview();
    manualMarkers = [];
    markerSequence = 0;
    selectedMarkerId = null;
    dragMarkerId = null;
    playheadSeconds = null;

    if (manualCutRangesInput) {
      manualCutRangesInput.value = "[]";
    }

    if (markerSummary) {
      markerSummary.hidden = true;
      markerSummary.textContent = "";
    }

    if (markerList) {
      markerList.hidden = true;
      markerList.innerHTML = "";
    }

    if (waveformOverlay) {
      waveformOverlay.innerHTML = "";
    }
  };

  const resetEditor = () => {
    waveformToken += 1;
    detectedSilenceCacheKey = "";
    detectedSilenceCache = [];
    disposePreviewAudio();
    resetManualCuts();
    waveformDurationSeconds = 0;
    waveformPeaks = null;
    playheadSeconds = null;
    selectedMarkerId = null;
    dragMarkerId = null;

    if (fileInput) {
      fileInput.value = "";
    }

    syncSelectedFileName();

    if (defaultState) {
      if (noiseSlider && defaultState.noiseSlider !== null) {
        noiseSlider.value = defaultState.noiseSlider;
      }

      if (silenceSlider && defaultState.silenceSlider !== null) {
        silenceSlider.value = defaultState.silenceSlider;
      }

      if (retainedSilenceSlider && defaultState.retainedSilenceSlider !== null) {
        retainedSilenceSlider.value = defaultState.retainedSilenceSlider;
      }

      if (cutHandleSlider && defaultState.cutHandleSlider !== null) {
        cutHandleSlider.value = defaultState.cutHandleSlider;
      }

      if (crossfadeSlider && defaultState.crossfadeSlider !== null) {
        crossfadeSlider.value = defaultState.crossfadeSlider;
      }

      if (videoCrossfadeSlider && defaultState.videoCrossfadeSlider !== null) {
        videoCrossfadeSlider.value = defaultState.videoCrossfadeSlider;
      }

      if (pauseSpeedSlider && defaultState.pauseSpeedSlider !== null) {
        pauseSpeedSlider.value = defaultState.pauseSpeedSlider;
      }

      if (waveformVerticalZoom) {
        waveformVerticalZoom.value = defaultState.waveformVerticalZoom;
      }

      if (waveformHorizontalZoom) {
        waveformHorizontalZoom.value = defaultState.waveformHorizontalZoom;
      }
    }

    syncNoiseControls();
    syncSilenceControls();
    syncRetainedSilenceControls();
    syncCutHandleControls();
    syncCrossfadeControls();
    syncVideoCrossfadeControls();
    syncPauseSpeedControls();
    syncWaveformZoomControls();
    updateThresholdReadout();
    setMarkerButtonsEnabled(false);
    clearWaveformCanvas();

    if (waveformViewport) {
      waveformViewport.scrollLeft = 0;
    }

    if (waveformPanel) {
      waveformPanel.hidden = true;
    }

    if (waveformStatus) {
      waveformStatus.textContent = "Choose a video file to analyze its audio.";
    }

    if (validationSummary) {
      validationSummary.innerHTML = "";
    }

    syncPlaybackButtons();
  };

  const getInsertionSeconds = () => {
    if (playheadSeconds !== null) {
      return playheadSeconds;
    }

    return waveformViewport ? xToSeconds(waveformViewport.scrollLeft + waveformViewport.clientWidth / 2) : 0;
  };

  const addMarker = (type) => {
    if (waveformDurationSeconds <= 0) {
      return;
    }

    const marker = {
      id: `marker-${++markerSequence}`,
      type,
      timeSeconds: clamp(getInsertionSeconds(), 0, waveformDurationSeconds),
    };

    manualMarkers.push(marker);
    selectedMarkerId = marker.id;
    syncManualCutRangesInput();
  };

  const removeMarker = (markerId) => {
    manualMarkers = manualMarkers.filter((marker) => marker.id !== markerId);
    if (selectedMarkerId === markerId) {
      selectedMarkerId = null;
    }

    if (dragMarkerId === markerId) {
      dragMarkerId = null;
    }

    syncManualCutRangesInput();
  };

  const updateMarkerTime = (markerId, timeSeconds) => {
    manualMarkers = manualMarkers.map((marker) =>
      marker.id === markerId
        ? { ...marker, timeSeconds: clamp(timeSeconds, 0, waveformDurationSeconds) }
        : marker,
    );

    selectedMarkerId = markerId;
    syncManualCutRangesInput();
  };

  const placePlayheadFromClientX = (clientX) => {
    if (!waveformStage || waveformDurationSeconds <= 0) {
      return;
    }

    const bounds = waveformStage.getBoundingClientRect();
    playheadSeconds = xToSeconds(clientX - bounds.left);

    if (previewAudio) {
      previewAudio.currentTime = playheadSeconds;
    }

    if (resultPreviewContext) {
      stopResultPreview();
    }

    renderWaveformOverlay();
  };

  const drawWaveform = () => {
    if (!waveformCanvas || !waveformPanel || !waveformViewport || !waveformPeaks || waveformPeaks.length === 0) {
      renderWaveformOverlay();
      return;
    }

    const previousScrollLeft = waveformViewport.scrollLeft;
    const previousScrollableWidth = Math.max(1, waveformViewport.scrollWidth - waveformViewport.clientWidth);
    const scrollRatio = previousScrollableWidth > 0 ? previousScrollLeft / previousScrollableWidth : 0;
    const viewportWidth = Math.max(320, Math.floor(waveformViewport.clientWidth || waveformPanel.clientWidth || 640));
    const cssWidth = Math.max(viewportWidth, Math.floor(viewportWidth * horizontalZoomValue));
    const cssHeight = 260;
    const pixelRatio = window.devicePixelRatio || 1;
    waveformCanvas.width = Math.floor(cssWidth * pixelRatio);
    waveformCanvas.height = Math.floor(cssHeight * pixelRatio);
    waveformCanvas.style.width = `${cssWidth}px`;
    waveformCanvas.style.height = `${cssHeight}px`;

    if (waveformStage) {
      waveformStage.style.width = `${cssWidth}px`;
      waveformStage.style.height = `${cssHeight}px`;
    }

    const context = waveformCanvas.getContext("2d");
    if (!context) {
      return;
    }

    context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    context.clearRect(0, 0, cssWidth, cssHeight);

    const centerY = cssHeight / 2;
    const drawableHalfHeight = ((cssHeight - 36) / 2) * verticalZoomValue;
    context.fillStyle = "rgba(10, 10, 11, 1)";
    context.fillRect(0, 0, cssWidth, cssHeight);
    context.strokeStyle = "rgba(255, 255, 255, 0.08)";
    context.beginPath();
    context.moveTo(0, centerY);
    context.lineTo(cssWidth, centerY);
    context.stroke();

    const thresholdDb = parseThresholdDb();
    if (thresholdDb !== null) {
      const amplitude = Math.max(0, Math.min(1, Math.pow(10, thresholdDb / 20)));
      const offset = amplitude * drawableHalfHeight;
      context.strokeStyle = "rgba(255, 108, 4, 0.9)";
      context.lineWidth = 1.5;
      context.setLineDash([8, 6]);
      context.beginPath();
      context.moveTo(0, centerY - offset);
      context.lineTo(cssWidth, centerY - offset);
      context.moveTo(0, centerY + offset);
      context.lineTo(cssWidth, centerY + offset);
      context.stroke();
      context.setLineDash([]);
    }

    context.strokeStyle = "rgba(242, 242, 242, 0.72)";
    context.lineWidth = 1;
    const columnWidth = cssWidth / waveformPeaks.length;

    for (let index = 0; index < waveformPeaks.length; index += 1) {
      const peak = waveformPeaks[index];
      const x = index * columnWidth + columnWidth / 2;
      context.beginPath();
      context.moveTo(x, centerY - peak.max * drawableHalfHeight);
      context.lineTo(x, centerY - peak.min * drawableHalfHeight);
      context.stroke();
    }

    renderWaveformOverlay();
    window.requestAnimationFrame(() => {
      const nextScrollableWidth = Math.max(0, waveformViewport.scrollWidth - waveformViewport.clientWidth);
      waveformViewport.scrollLeft = nextScrollableWidth * scrollRatio;
    });
  };

  const buildWaveformPeaks = (audioBuffer, sampleCount) => {
    const channelCount = audioBuffer.numberOfChannels;
    const channels = [];
    for (let index = 0; index < channelCount; index += 1) {
      channels.push(audioBuffer.getChannelData(index));
    }

    const blockSize = Math.max(1, Math.floor(audioBuffer.length / sampleCount));
    const peaks = [];
    for (let blockIndex = 0; blockIndex < sampleCount; blockIndex += 1) {
      const start = blockIndex * blockSize;
      const end = Math.min(start + blockSize, audioBuffer.length);
      let min = 1;
      let max = -1;

      for (let sampleIndex = start; sampleIndex < end; sampleIndex += 1) {
        let sample = 0;
        for (let channelIndex = 0; channelIndex < channelCount; channelIndex += 1) {
          sample += channels[channelIndex][sampleIndex] || 0;
        }

        sample /= channelCount;
        if (sample > max) {
          max = sample;
        }

        if (sample < min) {
          min = sample;
        }
      }

      peaks.push({ min, max });
    }

    return peaks;
  };

  const analyzeSelectedFile = async () => {
    if (!waveformPanel || !waveformCanvas || !waveformStatus) {
      return;
    }

    const file = fileInput.files && fileInput.files[0];
    const token = ++waveformToken;
    detectedSilenceCacheKey = "";
    detectedSilenceCache = [];
    updateThresholdReadout();
    resetManualCuts();
    waveformDurationSeconds = 0;
    waveformPeaks = null;
    setMarkerButtonsEnabled(false);

    if (!file) {
      disposePreviewAudio();
      waveformPanel.hidden = true;
      return;
    }

    initializePreviewAudio(file);
    waveformPanel.hidden = false;
    waveformStatus.textContent = "Analyzing local audio track...";
    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextCtor) {
      waveformStatus.textContent = "This browser cannot decode audio for waveform preview.";
      return;
    }

    let audioContext = null;
    try {
      const arrayBuffer = await file.arrayBuffer();
      if (token !== waveformToken) {
        return;
      }

      audioContext = new AudioContextCtor();
      const audioBuffer = await audioContext.decodeAudioData(arrayBuffer.slice(0));
      if (token !== waveformToken) {
        return;
      }

      decodedAudioBuffer = audioBuffer;
      waveformDurationSeconds = audioBuffer.duration;
      playheadSeconds = 0;
      waveformPeaks = buildWaveformPeaks(audioBuffer, Math.min(1200, Math.max(320, Math.floor(waveformPanel.clientWidth || 720))));
      waveformStatus.textContent = `${file.name} | ${formatDuration(audioBuffer.duration)} | preview only`;
      setMarkerButtonsEnabled(true);
      syncPlaybackButtons();
      drawWaveform();
    } catch {
      disposePreviewAudio();
      waveformDurationSeconds = 0;
      playheadSeconds = null;
      decodedAudioBuffer = null;
      waveformStatus.textContent = "Could not decode audio from this file in the browser.";
      renderWaveformOverlay();
    } finally {
      if (audioContext) {
        audioContext.close().catch(() => {});
      }
    }
  };

  if (noiseInput && noiseSlider) {
    const initialThreshold = parseThresholdDb();
    if (initialThreshold !== null) {
      noiseSlider.value = `${initialThreshold}`;
    }

    syncNoiseControls();
    noiseSlider.addEventListener("input", () => {
      stopResultPreview();
      syncNoiseControls();
      updateThresholdReadout();
      drawWaveform();
    });
  }

  if (silenceSlider) {
    syncSilenceControls();
    silenceSlider.addEventListener("input", () => {
      stopResultPreview();
      syncSilenceControls();
      renderWaveformOverlay();
    });
  }

  if (retainedSilenceSlider) {
    syncRetainedSilenceControls();
    retainedSilenceSlider.addEventListener("input", () => {
      stopResultPreview();
      syncRetainedSilenceControls();
      renderWaveformOverlay();
    });
  }

  if (cutHandleSlider) {
    syncCutHandleControls();
    cutHandleSlider.addEventListener("input", () => {
      stopResultPreview();
      syncCutHandleControls();
      renderWaveformOverlay();
    });
  }

  if (crossfadeSlider) {
    syncCrossfadeControls();
    crossfadeSlider.addEventListener("input", () => {
      stopResultPreview();
      syncCrossfadeControls();
    });
  }

  if (videoCrossfadeSlider) {
    syncVideoCrossfadeControls();
    videoCrossfadeSlider.addEventListener("input", () => {
      syncVideoCrossfadeControls();
    });
  }

  if (pauseSpeedSlider) {
    syncPauseSpeedControls();
    pauseSpeedSlider.addEventListener("input", () => {
      stopResultPreview();
      syncPauseSpeedControls();
      renderWaveformOverlay();
    });
  }

  syncWaveformZoomControls();
  setMarkerButtonsEnabled(false);

  if (waveformVerticalZoom) {
    waveformVerticalZoom.addEventListener("input", () => {
      syncWaveformZoomControls();
      drawWaveform();
    });
  }

  if (waveformHorizontalZoom) {
    waveformHorizontalZoom.addEventListener("input", () => {
      syncWaveformZoomControls();
      drawWaveform();
    });
  }

  addMarkerButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const markerType = button.getAttribute("data-add-marker");
      if (markerType === "in" || markerType === "out") {
        addMarker(markerType);
      }
    });
  });

  if (playAudioButton) {
    playAudioButton.addEventListener("click", async () => {
      if (!previewAudio) {
        return;
      }

      if (!previewAudio.paused) {
        stopPreviewAudio();
        return;
      }

      if (playheadSeconds !== null) {
        previewAudio.currentTime = clamp(playheadSeconds, 0, getPreviewDuration());
      }

      try {
        stopResultPreview();
        await previewAudio.play();
      } catch {
        waveformStatus.textContent = "The browser blocked audio playback for this file.";
        syncPlaybackButtons();
      }
    });
  }

  if (playResultButton) {
    playResultButton.addEventListener("click", () => {
      if (!decodedAudioBuffer) {
        return;
      }

      if (resultPreviewContext) {
        stopResultPreview();
        return;
      }

      stopPreviewAudio();

      const allSegments = buildResultPreviewSegments();
      if (allSegments.length === 0) {
        waveformStatus.textContent = "No audible result remains with the current threshold and cut markers.";
        return;
      }

      const startAtSourceSeconds = playheadSeconds ?? 0;
      const segmentsToPlay = allSegments
        .filter((segment) => segment.endSeconds > startAtSourceSeconds)
        .map((segment) => ({
          ...segment,
          startSeconds: Math.max(segment.startSeconds, startAtSourceSeconds),
        }))
        .map((segment, index) => ({
          ...segment,
          startsAfterHardCut:
            index > 0
              ? segment.startsAfterHardCut
              : false,
          outputDurationSeconds:
            (segment.endSeconds - segment.startSeconds) / (segment.playbackSpeed || 1),
        }))
        .filter((segment) => segment.endSeconds - segment.startSeconds > 0.01);

      if (segmentsToPlay.length === 0) {
        waveformStatus.textContent = "The current playhead is past the remaining kept audio.";
        return;
      }

      const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextCtor) {
        waveformStatus.textContent = "This browser cannot preview the result audio.";
        return;
      }

      const requestedCrossfadeSeconds = Math.max(0, getCrossfadeMilliseconds()) / 1000;
      const appliedCrossfadeSeconds = determineAppliedCrossfadeSeconds(segmentsToPlay, requestedCrossfadeSeconds);
      const audioContext = new AudioContextCtor();
      const baseTime = audioContext.currentTime + 0.05;
      const scheduledSources = [];
      const scheduledEntries = [];
      let resultStartSeconds = 0;

      segmentsToPlay.forEach((segment, index) => {
        const source = audioContext.createBufferSource();
        const gain = audioContext.createGain();
        const durationSeconds = segment.endSeconds - segment.startSeconds;
        const outputDurationSeconds = segment.outputDurationSeconds ?? durationSeconds;
        const fadeInSeconds = index > 0 && segment.startsAfterHardCut ? appliedCrossfadeSeconds : 0;
        const nextSegment = segmentsToPlay[index + 1];
        const fadeOutSeconds = nextSegment && nextSegment.startsAfterHardCut ? appliedCrossfadeSeconds : 0;
        const nodeStartTime = baseTime + resultStartSeconds;
        const nodeEndTime = nodeStartTime + outputDurationSeconds;

        source.buffer = decodedAudioBuffer;
        source.playbackRate.value = segment.playbackSpeed || 1;
        source.connect(gain);
        gain.connect(audioContext.destination);

        gain.gain.setValueAtTime(fadeInSeconds > 0 ? 0 : 1, nodeStartTime);
        if (fadeInSeconds > 0) {
          gain.gain.linearRampToValueAtTime(1, nodeStartTime + fadeInSeconds);
        }

        if (fadeOutSeconds > 0) {
          gain.gain.setValueAtTime(1, Math.max(nodeStartTime, nodeEndTime - fadeOutSeconds));
          gain.gain.linearRampToValueAtTime(0, nodeEndTime);
        }

        source.start(nodeStartTime, segment.startSeconds, durationSeconds);
        scheduledSources.push(source);
        scheduledEntries.push({
          resultStartSeconds,
          resultEndSeconds: resultStartSeconds + outputDurationSeconds,
          sourceStartSeconds: segment.startSeconds,
          sourceEndSeconds: segment.endSeconds,
          playbackSpeed: segment.playbackSpeed || 1,
        });

        resultStartSeconds += outputDurationSeconds - fadeOutSeconds;
      });

      resultPreviewContext = audioContext;
      resultPreviewSources = scheduledSources;
      resultPreviewEntries = scheduledEntries;
      resultPreviewBaseTime = baseTime;
      playheadSeconds = segmentsToPlay[0].startSeconds;
      waveformStatus.textContent = "Previewing the current result settings.";
      syncPlaybackButtons();
      stopResultPlaybackTracking();
      trackResultPlayback();
    });
  }

  if (waveformStage) {
    waveformStage.addEventListener("click", (event) => {
      const target = event.target;
      if (target instanceof HTMLElement && target.closest("[data-marker-id]")) {
        return;
      }

      placePlayheadFromClientX(event.clientX);
    });
  }

  if (waveformOverlay) {
    waveformOverlay.addEventListener("click", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) {
        return;
      }

      const markerButton = target.closest("[data-marker-id]");
      if (!markerButton) {
        return;
      }

      selectedMarkerId = markerButton.getAttribute("data-marker-id");
      renderWaveformOverlay();
      renderMarkerList(sortMarkers());
    });

    waveformOverlay.addEventListener("dblclick", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) {
        return;
      }

      const markerButton = target.closest("[data-marker-id]");
      if (!markerButton) {
        return;
      }

      event.preventDefault();
      removeMarker(markerButton.getAttribute("data-marker-id"));
    });

    waveformOverlay.addEventListener("pointerdown", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) {
        return;
      }

      const markerButton = target.closest("[data-marker-id]");
      if (!markerButton) {
        return;
      }

      event.preventDefault();
      dragMarkerId = markerButton.getAttribute("data-marker-id");
      selectedMarkerId = dragMarkerId;
      renderWaveformOverlay();
      renderMarkerList(sortMarkers());
    });
  }

  window.addEventListener("pointermove", (event) => {
    if (!dragMarkerId || !waveformStage) {
      return;
    }

    const bounds = waveformStage.getBoundingClientRect();
    updateMarkerTime(dragMarkerId, xToSeconds(event.clientX - bounds.left));
  });

  window.addEventListener("pointerup", () => {
    dragMarkerId = null;
  });

  window.addEventListener("keydown", (event) => {
    const activeTagName = document.activeElement ? document.activeElement.tagName : "";
    if (
      activeTagName === "INPUT" ||
      activeTagName === "TEXTAREA" ||
      activeTagName === "SELECT" ||
      (document.activeElement instanceof HTMLElement && document.activeElement.isContentEditable)
    ) {
      return;
    }

    if (event.key === " " || event.code === "Space") {
      event.preventDefault();

      if (resultPreviewContext) {
        stopResultPreview();
        return;
      }

      if (!previewAudio) {
        return;
      }

      if (!previewAudio.paused) {
        stopPreviewAudio();
        return;
      }

      if (playheadSeconds !== null) {
        previewAudio.currentTime = clamp(playheadSeconds, 0, getPreviewDuration());
      }

      stopResultPreview();
      previewAudio.play().catch(() => {
        waveformStatus.textContent = "The browser blocked audio playback for this file.";
        syncPlaybackButtons();
      });
      return;
    }

    if (event.key === "r" || event.key === "R") {
      event.preventDefault();
      if (playResultButton) {
        playResultButton.click();
      }
      return;
    }

    if (event.key === "a" || event.key === "A") {
      event.preventDefault();
      addMarker("in");
      return;
    }

    if (event.key === "s" || event.key === "S") {
      event.preventDefault();
      addMarker("out");
      return;
    }

    if (!selectedMarkerId) {
      return;
    }

    if (event.key === "Delete" || event.key === "Backspace") {
      event.preventDefault();
      removeMarker(selectedMarkerId);
    }
  });

  if (markerList) {
    markerList.addEventListener("click", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) {
        return;
      }

      const removeButton = target.closest("[data-marker-remove]");
      if (removeButton) {
        removeMarker(removeButton.getAttribute("data-marker-remove"));
        return;
      }

      const focusButton = target.closest("[data-marker-focus]");
      if (!focusButton) {
        return;
      }

      selectedMarkerId = focusButton.getAttribute("data-marker-focus");
      const selectedMarker = manualMarkers.find((marker) => marker.id === selectedMarkerId);
      if (selectedMarker) {
        playheadSeconds = selectedMarker.timeSeconds;
        if (previewAudio) {
          previewAudio.currentTime = selectedMarker.timeSeconds;
        }

        if (resultPreviewContext) {
          stopResultPreview();
        }
      }

      renderWaveformOverlay();
      renderMarkerList(sortMarkers());
    });
  }

  fileInput.addEventListener("change", () => {
    syncSelectedFileName();
    analyzeSelectedFile();
  });

  if (pickFileButton instanceof HTMLButtonElement) {
    pickFileButton.addEventListener("click", () => {
      fileInput.click();
    });
  }

  if (resetAllButton instanceof HTMLButtonElement) {
    resetAllButton.addEventListener("click", () => {
      resetEditor();
    });
  }

  window.addEventListener("resize", () => {
    drawWaveform();
  });

  updateThresholdReadout();
  resetManualCuts();
  syncSelectedFileName();
  syncPlaybackButtons();
  defaultState = captureDefaultState();
  window.addEventListener("beforeunload", () => {
    disposePreviewAudio();
  });

  form.addEventListener("submit", (event) => {
    if (!fileInput.files || fileInput.files.length === 0) {
      return;
    }

    if (!form.reportValidity()) {
      event.preventDefault();
      return;
    }

    stopPreviewAudio();
    stopResultPreview();
    event.preventDefault();
    processingPanel.hidden = false;
    form.classList.add("is-processing");
    submitButton.disabled = true;
    if (validationSummary) {
      validationSummary.textContent = "";
    }

    setProgress(3, "Preparing upload...");
    const request = new XMLHttpRequest();
    request.open(form.method || "POST", form.action || window.location.href, true);
    request.responseType = "text";
    request.setRequestHeader("X-Requested-With", "XMLHttpRequest");

    request.upload.addEventListener("progress", (uploadEvent) => {
      if (!uploadEvent.lengthComputable) {
        setProgress(Math.max(progressValue, 14), "Uploading video...");
        return;
      }

      setProgress(Math.max(6, (uploadEvent.loaded / uploadEvent.total) * 38), "Uploading video...");
    });

    request.upload.addEventListener("load", () => {
      setProgress(Math.max(progressValue, 40), "Upload complete. Starting analysis...");
      beginProcessingSimulation();
    });

    request.addEventListener("load", () => {
      clearProcessingTimer();
      if (request.status >= 200 && request.status < 300 && typeof request.responseText === "string") {
        setProgress(100, "Export complete.");
        applyServerResponse(request.responseText);
        finishProcessing();
        return;
      }

      showFailure("The server returned an error while processing the video.");
    });

    request.addEventListener("error", () => {
      showFailure("The browser lost contact with the local app while processing the video.");
    });

    request.addEventListener("abort", () => {
      showFailure("The upload was cancelled.");
    });

    request.send(new FormData(form));
  });
})();
