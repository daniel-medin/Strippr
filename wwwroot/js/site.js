(() => {
  const markExportConsumed = (trigger, message) => {
    const resultPanel = trigger instanceof HTMLElement
      ? trigger.closest("[data-result-panel]")
      : null;
    const saveStatus = resultPanel?.querySelector("[data-save-status]");

    resultPanel?.querySelectorAll("[data-save-file], .download-actions a[download]").forEach((element) => {
      if (element instanceof HTMLButtonElement) {
        element.disabled = true;
        return;
      }

      if (element instanceof HTMLAnchorElement) {
        element.setAttribute("aria-disabled", "true");
        element.classList.add("is-disabled");
      }
    });

    if (saveStatus instanceof HTMLElement) {
      saveStatus.hidden = false;
      saveStatus.textContent = message;
    }
  };

  const fallbackDownload = (downloadUrl, fileName) => {
    const link = document.createElement("a");
    link.href = downloadUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  };

  document.addEventListener("click", async (event) => {
    const saveButton = event.target instanceof HTMLElement
      ? event.target.closest("[data-save-file]")
      : null;
    if (!(saveButton instanceof HTMLButtonElement)) {
      return;
    }

    const saveStatus = document.querySelector("[data-save-status]");
    if (!(saveStatus instanceof HTMLElement)) {
      return;
    }

    event.preventDefault();

    const downloadUrl = saveButton.getAttribute("data-download-url");
    const fileName = saveButton.getAttribute("data-file-name") || "strippr-output.mp4";

    if (!downloadUrl) {
      return;
    }

    saveButton.disabled = true;
    saveStatus.hidden = false;
    saveStatus.textContent = "Preparing file...";
    let exportConsumed = false;

    try {
      if (!("showSaveFilePicker" in window)) {
        fallbackDownload(downloadUrl, fileName);
        saveStatus.textContent = "Browser save picker not available. Used normal download instead.";
        return;
      }

      const handle = await window.showSaveFilePicker({
        suggestedName: fileName,
        types: [{ description: "Video file", accept: { "video/mp4": [".mp4", ".mov", ".m4v"] } }],
      });

      saveStatus.textContent = "Downloading file...";
      const response = await fetch(downloadUrl, { credentials: "same-origin" });
      if (!response.ok) {
        throw new Error("Download failed.");
      }

      const writable = await handle.createWritable();
      await writable.write(await response.blob());
      await writable.close();
      exportConsumed = true;
      markExportConsumed(saveButton, "Saved successfully. The temporary render has been removed from Strippr.");
    } catch (error) {
      if (error && error.name === "AbortError") {
        saveStatus.textContent = "Save cancelled.";
      } else if (error && error.name === "SecurityError") {
        fallbackDownload(downloadUrl, fileName);
        saveStatus.textContent = "Browser blocked the save picker. Used normal download instead.";
      } else {
        fallbackDownload(downloadUrl, fileName);
        saveStatus.textContent = "Save picker failed. Used normal download instead.";
      }
    } finally {
      if (!exportConsumed) {
        saveButton.disabled = false;
      }
    }
  });

  document.addEventListener("click", (event) => {
    const downloadLink = event.target instanceof HTMLElement
      ? event.target.closest(".download-actions a[download]")
      : null;

    if (!(downloadLink instanceof HTMLAnchorElement)) {
      return;
    }

    window.setTimeout(() => {
      markExportConsumed(downloadLink, "Download started. This temporary render has been removed from Strippr.");
    }, 250);
  });
})();
