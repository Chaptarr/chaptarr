const defaultFilename = 'chaptarrng_backup.enc';

function getFilename(response) {
  const contentDisposition = response.headers.get('content-disposition') || '';
  const filenamePattern = /filename="?([^";]+)"?/i;
  const match = filenamePattern.exec(contentDisposition);

  return match ? match[1] : defaultFilename;
}

export default async function downloadEncryptedBackup(passphrase, fallbackMessage) {
  const apiRoot = window.Chaptarr.apiRoot.replace(/\/$/, '');
  const response = await window.fetch(`${apiRoot}/system/backup/encrypted`, {
    method: 'POST',
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      'X-Api-Key': window.Chaptarr.apiKey,
      'X-ChaptarrNG-Client': 'native',
      'X-Bookshelf-Backup-Passphrase': passphrase
    },
    body: '{}'
  });

  if (!response.ok) {
    let message = fallbackMessage;

    try {
      const error = await response.json();
      message = error.message || error.Message || message;
    } catch (error) {
      // Keep the generic message if the server response is not JSON.
    }

    throw new Error(message);
  }

  const blob = await response.blob();
  const objectUrl = window.URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = objectUrl;
  link.download = getFilename(response);
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => window.URL.revokeObjectURL(objectUrl), 1000);
}
