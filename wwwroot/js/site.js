(() => {
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
      saveStatus.textContent = "Saved successfully.";
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
      saveButton.disabled = false;
    }
  });
})();
