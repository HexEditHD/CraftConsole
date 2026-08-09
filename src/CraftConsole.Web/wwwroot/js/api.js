// Thin fetch wrapper for the CraftConsole REST API.
async function handleResponse(res) {
  if (res.status === 401) {
    // Session expired or the server restarted (in-memory sessions don't survive
    // that). Reload — the auth gate serves the login page for any signed-out GET.
    location.reload();
    throw new Error('Session expired');
  }

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const data = await res.json();
      if (data?.message) message = data.message;
    } catch { /* non-JSON error body */ }
    throw Object.assign(new Error(message), { status: res.status });
  }

  if (res.status === 204 || res.status === 202) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

async function request(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  return handleResponse(res);
}

// Multipart upload — no Content-Type header, the browser sets the boundary itself.
async function upload(url, formData) {
  const res = await fetch(url, { method: 'POST', body: formData });
  return handleResponse(res);
}

// fetch() has no upload progress event, so a large file transfer needs XHR
// instead — mirrors handleResponse's status/body handling since there's no
// Response object here to hand off to it.
function uploadWithProgress(url, formData, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.upload.onprogress = e => {
      if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total);
    };
    xhr.onerror = () => reject(new Error('Upload failed — connection lost.'));
    xhr.onload = () => {
      if (xhr.status === 401) { location.reload(); return; }
      if (xhr.status === 204 || xhr.status === 202) { resolve(null); return; }
      let data = null;
      try { data = xhr.responseText ? JSON.parse(xhr.responseText) : null; } catch { /* non-JSON body */ }
      if (xhr.status >= 200 && xhr.status < 300) resolve(data);
      else reject(Object.assign(new Error(data?.message ?? `${xhr.status} ${xhr.statusText}`), { status: xhr.status }));
    };
    xhr.send(formData);
  });
}

export const api = {
  get: url => request('GET', url),
  post: (url, body) => request('POST', url, body),
  put: (url, body) => request('PUT', url, body),
  del: url => request('DELETE', url),
  upload: (url, formData) => upload(url, formData),
  uploadWithProgress: (url, formData, onProgress) => uploadWithProgress(url, formData, onProgress),
};
