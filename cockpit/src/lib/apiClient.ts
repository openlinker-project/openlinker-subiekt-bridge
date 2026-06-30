const API_URL = '/api'

// Shared API key for the bridge (secure-by-default: every /api/* call needs it after the
// Faza-0 hardening). Supplied at build/dev time via VITE_API_KEY and sent as the
// `Authorization: Bearer <key>` header — the ONLY scheme the bridge accepts;
// /health stays anonymous. Empty in a dev setup where the bridge runs with Auth disabled.
const API_KEY = (import.meta.env.VITE_API_KEY as string | undefined) ?? ''

export interface ApiResponse<T> {
  success: boolean
  data?: T
  error?: string
  errorCode?: string
}

export interface HealthResponse {
  status: string
  bridge: string
  sferaSession: string
  subiekt: string
  subiektError?: string | null
  time: string
}

export async function apiCall<T>(
  method: string,
  path: string,
  body?: unknown
): Promise<ApiResponse<T>> {
  const url = `${API_URL}${path}`

  const options: RequestInit = {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(API_KEY ? { Authorization: `Bearer ${API_KEY}` } : {}),
    },
  }

  if (body && (method === 'POST' || method === 'PUT')) {
    options.body = JSON.stringify(body)
  }

  try {
    const response = await fetch(url, options)
    const text = await response.text()

    console.log(`API ${method} ${url}:`, {
      status: response.status,
      ok: response.ok,
      responseText: text,
    })

    let data
    try {
      data = text ? JSON.parse(text) : {}
    } catch (e) {
      console.error('JSON parse failed:', e, 'text:', text)
      return {
        success: false,
        error: `Invalid JSON: ${text.substring(0, 100)}`,
      }
    }

    if (!response.ok) {
      // Bridge now returns a structured error { code, reason }. Flatten it to a
      // string so pages that render `result.error` directly don't choke on an
      // object, while keeping the code available for callers that want it.
      const rawErr = data.error ?? data.detail
      const errStr =
        rawErr && typeof rawErr === 'object'
          ? `${rawErr.code ? rawErr.code + ': ' : ''}${rawErr.reason ?? JSON.stringify(rawErr)}`
          : rawErr || `HTTP ${response.status}`
      return {
        success: false,
        error: errStr,
        errorCode: rawErr && typeof rawErr === 'object' ? rawErr.code : undefined,
      }
    }

    // Success path may also carry a structured error=null — normalize.
    if (data && typeof data === 'object' && data.error && typeof data.error === 'object') {
      data.error = `${data.error.code ? data.error.code + ': ' : ''}${data.error.reason ?? ''}`
    }
    return data as ApiResponse<T>
  } catch (error) {
    return {
      success: false,
      error: error instanceof Error ? error.message : 'Unknown error',
    }
  }
}

export const api = {
  // Bridge health (3-state). /health is outside the /api prefix.
  health: async (): Promise<ApiResponse<HealthResponse>> => {
    try {
      const res = await fetch('/health')
      const data = await res.json()
      return { success: res.ok, data }
    } catch (e) {
      return { success: false, error: e instanceof Error ? e.message : 'unreachable' }
    }
  },

  // Sfera connection
  connect: () => apiCall('POST', '/session/connect'),
  status: () => apiCall('GET', '/session/status'),

  // Read operations
  towary: (limit = 100) =>
    apiCall('GET', '/products?limit=' + limit),
  kontrahenci: (limit = 100) =>
    apiCall('GET', '/customers?limit=' + limit),
  magazyny: () =>
    apiCall('GET', '/warehouses'),
  stany: (magazyn?: string, symbol?: string) => {
    const params = new URLSearchParams()
    if (magazyn) params.append('magazyn', magazyn)
    if (symbol) params.append('symbol', symbol)
    params.append('limit', '500')   // endpoint requires a non-null int limit
    return apiCall(
      'GET',
      `/stock?${params.toString()}`
    )
  },

  // Write operations
  createTowar: (data: unknown) =>
    apiCall('POST', '/products', data),
  towarBySymbol: (symbol: string) =>
    apiCall('GET', `/products/${encodeURIComponent(symbol)}`),
  upsertKontrahent: (data: unknown) =>
    apiCall('POST', '/customers/upsert', data),
  createFaktura: (data: unknown) =>
    apiCall('POST', '/invoices', data),
  invoiceStatus: (id: string) =>
    apiCall('GET', `/invoices/${id}/status`),
  korekta: (id: number | string, data: unknown) =>
    apiCall('POST', `/invoices/${id}/corrections`, data),
  auditLast: (limit = 10) =>
    apiCall('GET', `/audit/last?limit=${limit}`),
}
