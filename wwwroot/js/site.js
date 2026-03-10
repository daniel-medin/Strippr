(() => {
  const form = document.querySelector("[data-processing-form]");

  if (form) {
    const processingPanel = form.querySelector("[data-processing-panel]");
    const processingFill = form.querySelector("[data-processing-fill]");
    const processingLabel = form.querySelector("[data-processing-label]");
    const processingPercent = form.querySelector("[data-processing-percent]");
    const processingBar = form.querySelector("[data-processing-bar]");
    const fileInput = form.querySelector("input[type='file']");
    const noiseInput = form.querySelector("input[name='Input.NoiseThreshold']");
    const noiseSlider = form.querySelector("[data-noise-slider]");
    const noiseDisplay = form.querySelector("[data-noise-display]");
    const silenceSlider = form.querySelector("[data-silence-slider]");
    const silenceDisplay = form.querySelector("[data-silence-display]");
    const validationSummary = form.querySelector(".validation-summary");
    const submitButton = form.querySelector("button[type='submit']");
    const waveformPanel = form.querySelector("[data-waveform-panel]");
    const waveformViewport = form.querySelector("[data-waveform-viewport]");
    const waveformCanvas = form.querySelector("[data-waveform-canvas]");
    const waveformStatus = form.querySelector("[data-waveform-status]");
    const thresholdReadout = form.querySelector("[data-threshold-readout]");
    const waveformVerticalZoom = form.querySelector("[data-waveform-vertical-zoom]");
    const waveformVerticalDisplay = form.querySelector("[data-waveform-vertical-display]");
    const waveformHorizontalZoom = form.querySelector("[data-waveform-horizontal-zoom]");
    const waveformHorizontalDisplay = form.querySelector("[data-waveform-horizontal-display]");

    if (
      processingPanel &&
      processingFill &&
      processingLabel &&
      processingPercent &&
      processingBar &&
      fileInput &&
      submitButton
    ) {
      let progressValue = 0;
      let processingTimerId = null;
      let waveformPeaks = null;
      let waveformToken = 0;
      let verticalZoomValue = 1;
      let horizontalZoomValue = 1;

      const setProgress = (value, text) => {
        progressValue = Math.max(0, Math.min(100, value));
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
            return;
          }

          if (progressValue < 84) {
            setProgress(progressValue + 1.6, "Rendering cleaned video...");
            return;
          }

          if (progressValue < 96) {
            setProgress(progressValue + 0.45, "Finalizing output...");
          }
        }, 350);
      };

      const showFailure = (message) => {
        clearProcessingTimer();
        submitButton.disabled = false;
        form.classList.remove("is-processing");
        processingLabel.textContent = "Processing failed.";
        processingPercent.textContent = `${Math.round(progressValue)}%`;

        if (validationSummary) {
          validationSummary.textContent = message;
        }
      };

      const parseThresholdDb = () => {
        if (!noiseInput) {
          return -30;
        }

        const match = `${noiseInput.value}`.trim().match(/(-?\d+(?:\.\d+)?)\s*dB/i);
        return match ? Number.parseFloat(match[1]) : null;
      };

      const formatDuration = (seconds) => {
        const totalSeconds = Math.max(0, Math.round(seconds));
        const hours = Math.floor(totalSeconds / 3600)
          .toString()
          .padStart(2, "0");
        const minutes = Math.floor((totalSeconds % 3600) / 60)
          .toString()
          .padStart(2, "0");
        const remainingSeconds = (totalSeconds % 60).toString().padStart(2, "0");
        return `${hours}:${minutes}:${remainingSeconds}`;
      };

      const updateThresholdReadout = () => {
        if (!thresholdReadout) {
          return;
        }

        const thresholdDb = parseThresholdDb();
        if (thresholdDb === null) {
          thresholdReadout.textContent = "Invalid dB";
          return;
        }

        const amplitudeRatio = Math.pow(10, thresholdDb / 20) * 100;
        thresholdReadout.textContent = `${thresholdDb.toFixed(1)} dB (${amplitudeRatio.toFixed(2)}% amp)`;
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
      };

      const syncSilenceControls = () => {
        if (!silenceSlider) {
          return;
        }

        const sliderValue = Number.parseFloat(silenceSlider.value);

        if (silenceDisplay) {
          silenceDisplay.textContent = `${sliderValue.toFixed(1)} s`;
        }
      };

      const syncWaveformZoomControls = () => {
        if (waveformVerticalZoom) {
          verticalZoomValue = Number.parseFloat(waveformVerticalZoom.value);

          if (waveformVerticalDisplay) {
            waveformVerticalDisplay.textContent = `${verticalZoomValue.toFixed(1)}x`;
          }
        }

        if (waveformHorizontalZoom) {
          horizontalZoomValue = Number.parseFloat(waveformHorizontalZoom.value);

          if (waveformHorizontalDisplay) {
            waveformHorizontalDisplay.textContent = `${horizontalZoomValue.toFixed(1)}x`;
          }
        }
      };

      const drawWaveform = () => {
        if (!waveformCanvas || !waveformPanel || !waveformViewport || !waveformPeaks || waveformPeaks.length === 0) {
          return;
        }

        const previousScrollLeft = waveformViewport.scrollLeft;
        const previousScrollableWidth = Math.max(
          1,
          waveformViewport.scrollWidth - waveformViewport.clientWidth,
        );
        const scrollRatio = previousScrollableWidth > 0 ? previousScrollLeft / previousScrollableWidth : 0;

        const viewportWidth = Math.max(320, Math.floor(waveformViewport.clientWidth || waveformPanel.clientWidth || 640));
        const cssWidth = Math.max(viewportWidth, Math.floor(viewportWidth * horizontalZoomValue));
        const cssHeight = 220;
        const pixelRatio = window.devicePixelRatio || 1;
        waveformCanvas.width = Math.floor(cssWidth * pixelRatio);
        waveformCanvas.height = Math.floor(cssHeight * pixelRatio);
        waveformCanvas.style.width = `${cssWidth}px`;
        waveformCanvas.style.height = `${cssHeight}px`;

        const context = waveformCanvas.getContext("2d");
        if (!context) {
          return;
        }

        context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
        context.clearRect(0, 0, cssWidth, cssHeight);

        const centerY = cssHeight / 2;
        const topPadding = 18;
        const bottomPadding = 18;
        const drawableHeight = cssHeight - topPadding - bottomPadding;
        const halfHeight = (drawableHeight / 2) * verticalZoomValue;

        context.fillStyle = "rgba(255, 249, 239, 0.95)";
        context.fillRect(0, 0, cssWidth, cssHeight);

        context.strokeStyle = "rgba(32, 24, 19, 0.10)";
        context.lineWidth = 1;
        context.beginPath();
        context.moveTo(0, centerY);
        context.lineTo(cssWidth, centerY);
        context.stroke();

        const thresholdDb = parseThresholdDb();
        if (thresholdDb !== null) {
          const amplitude = Math.max(0, Math.min(1, Math.pow(10, thresholdDb / 20)));
          const offset = amplitude * halfHeight;
          const upperY = centerY - offset;
          const lowerY = centerY + offset;

          context.strokeStyle = "rgba(220, 95, 49, 0.85)";
          context.lineWidth = 1.5;
          context.setLineDash([8, 6]);
          context.beginPath();
          context.moveTo(0, upperY);
          context.lineTo(cssWidth, upperY);
          context.moveTo(0, lowerY);
          context.lineTo(cssWidth, lowerY);
          context.stroke();
          context.setLineDash([]);
        }

        context.strokeStyle = "rgba(32, 24, 19, 0.75)";
        context.lineWidth = 1;

        const columnWidth = cssWidth / waveformPeaks.length;

        for (let index = 0; index < waveformPeaks.length; index += 1) {
          const peak = waveformPeaks[index];
          const x = index * columnWidth + columnWidth / 2;
          const yTop = centerY - peak.max * halfHeight;
          const yBottom = centerY - peak.min * halfHeight;

          context.beginPath();
          context.moveTo(x, yTop);
          context.lineTo(x, yBottom);
          context.stroke();
        }

        window.requestAnimationFrame(() => {
          const nextScrollableWidth = Math.max(
            0,
            waveformViewport.scrollWidth - waveformViewport.clientWidth,
          );
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
        updateThresholdReadout();

        if (!file) {
          waveformPanel.hidden = true;
          waveformPeaks = null;
          return;
        }

        waveformPanel.hidden = false;
        waveformStatus.textContent = "Analyzing local audio track...";

        const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextCtor) {
          waveformStatus.textContent = "This browser cannot decode audio for waveform preview.";
          waveformPeaks = null;
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

          const desiredSamples = Math.min(
            1200,
            Math.max(320, Math.floor(waveformPanel.clientWidth || 720)),
          );

          waveformPeaks = buildWaveformPeaks(audioBuffer, desiredSamples);
          waveformStatus.textContent = `${file.name} | ${formatDuration(audioBuffer.duration)} | preview only`;
          drawWaveform();
        } catch {
          waveformPeaks = null;
          waveformStatus.textContent = "Could not decode audio from this file in the browser.";
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
          syncNoiseControls();
          updateThresholdReadout();
          drawWaveform();
        });
      }

      if (silenceSlider) {
        syncSilenceControls();

        silenceSlider.addEventListener("input", () => {
          syncSilenceControls();
        });
      }

      syncWaveformZoomControls();

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

      fileInput.addEventListener("change", () => {
        analyzeSelectedFile();
      });

      window.addEventListener("resize", () => {
        drawWaveform();
      });

      updateThresholdReadout();

      form.addEventListener("submit", (event) => {
        if (!fileInput.files || fileInput.files.length === 0) {
          return;
        }

        if (!form.reportValidity()) {
          event.preventDefault();
          return;
        }

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

          const uploadProgress = (uploadEvent.loaded / uploadEvent.total) * 38;
          setProgress(Math.max(6, uploadProgress), "Uploading video...");
        });

        request.upload.addEventListener("load", () => {
          setProgress(Math.max(progressValue, 40), "Upload complete. Starting analysis...");
          beginProcessingSimulation();
        });

        request.addEventListener("load", () => {
          clearProcessingTimer();

          if (request.status >= 200 && request.status < 300 && typeof request.responseText === "string") {
            setProgress(100, "Reloading result...");
            document.open();
            document.write(request.responseText);
            document.close();
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
    }
  }

  const saveButton = document.querySelector("[data-save-file]");
  const saveStatus = document.querySelector("[data-save-status]");

  if (!saveButton || !saveStatus) {
    return;
  }

  const fallbackDownload = (downloadUrl, fileName) => {
    const link = document.createElement("a");
    link.href = downloadUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  };

  saveButton.addEventListener("click", async () => {
    const downloadUrl = saveButton.getAttribute("data-download-url");
    const fileName = saveButton.getAttribute("data-file-name") || "strippr-output.mp4";

    if (!downloadUrl) {
      return;
    }

    saveButton.disabled = true;
    saveStatus.hidden = false;
    saveStatus.textContent = "Preparing file...";

    try {
      if (!("showSaveFilePicker" in window)) {
        fallbackDownload(downloadUrl, fileName);
        saveStatus.textContent = "Browser save picker not available. Used normal download instead.";
        return;
      }

      const handle = await window.showSaveFilePicker({
        suggestedName: fileName,
        types: [
          {
            description: "Video file",
            accept: {
              "video/mp4": [".mp4", ".mov", ".m4v"],
            },
          },
        ],
      });

      saveStatus.textContent = "Downloading file...";

      const response = await fetch(downloadUrl, { credentials: "same-origin" });
      if (!response.ok) {
        throw new Error("Download failed.");
      }

      const blob = await response.blob();
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();

      saveStatus.textContent = "Saved successfully.";
    } catch (error) {
      if (error && error.name === "AbortError") {
        saveStatus.textContent = "Save cancelled.";
        return;
      }

      if (error && error.name === "SecurityError") {
        fallbackDownload(downloadUrl, fileName);
        saveStatus.textContent = "Browser blocked the save picker. Used normal download instead.";
        return;
      }

      fallbackDownload(downloadUrl, fileName);
      saveStatus.textContent = "Save picker failed. Used normal download instead.";
    } finally {
      saveButton.disabled = false;
    }
  });
})();
