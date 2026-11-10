export function downloadDatabaseFile(filename: string, contents: string, mimeType: string) {
  downloadDatabaseBlob(filename, new Blob([contents], { type: mimeType }));
}

export function downloadDatabaseBlob(filename: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename.replace(/[^a-zA-Z0-9_.-]/g, "_");
  document.body.appendChild(link);
  try { link.click(); } finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
}
