import { useState, useEffect } from 'react'
import { api, type HealthResponse } from '../lib/apiClient'
import { JsonResponse } from '../components/JsonResponse'
import { FormButton, FormLayout } from '../components/FormLayout'

export default function ConnectPage() {
  const [loading, setLoading] = useState(false)
  const [connected, setConnected] = useState<boolean | undefined>()
  const [response, setResponse] = useState<any>(null)
  const [error, setError] = useState<string | undefined>()
  const [health, setHealth] = useState<HealthResponse | undefined>()

  const refreshHealth = async () => {
    const res = await api.health()
    if (res.success && res.data) setHealth(res.data)
  }

  useEffect(() => {
    refreshHealth()
  }, [])

  const handleConnect = async () => {
    setLoading(true)
    setResponse(null)
    setError(undefined)

    try {
      const result = await api.connect()
      setResponse(result)
      if (result.success) {
        setConnected(true)
        setError(undefined)
      } else {
        setError(result.error)
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      setError(msg)
      setResponse({ error: msg })
    } finally {
      setLoading(false)
    }
  }

  const handleStatus = async () => {
    setLoading(true)
    setResponse(null)
    setError(undefined)

    try {
      const result = await api.status()
      setResponse(result)
      if (result.success) {
        setConnected((result.data as any)?.connected || false)
        setError(undefined)
      } else {
        setError(result.error)
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      setError(msg)
      setResponse({ error: msg })
    } finally {
      setLoading(false)
    }
  }

  return (
    <FormLayout title="Sfera Connection">
      <div className="space-y-4">
        {/* 3-state health: bridge / Sfera session / Subiekt(SQL) */}
        <div className="p-3 rounded border border-gray-200 bg-white">
          <div className="flex items-center justify-between mb-2">
            <span className="font-semibold text-sm">/health</span>
            <button
              type="button"
              onClick={refreshHealth}
              className="text-xs px-2 py-1 bg-gray-200 rounded hover:bg-gray-300"
            >
              Odśwież
            </button>
          </div>
          {health ? (
            <div className="flex flex-wrap gap-2 text-xs">
              {[
                { label: 'bridge', val: health.bridge, ok: health.bridge === 'up' },
                { label: 'sesja Sfery', val: health.sferaSession, ok: health.sferaSession === 'valid' },
                { label: 'Subiekt/SQL', val: health.subiekt, ok: health.subiekt === 'reachable' },
              ].map((s) => (
                <span
                  key={s.label}
                  className={`px-2 py-1 rounded font-medium ${
                    s.ok ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'
                  }`}
                  title={health.subiektError || ''}
                >
                  {s.label}: {s.val}
                </span>
              ))}
              <span className="px-2 py-1 rounded bg-gray-100 text-gray-700">status: {health.status}</span>
            </div>
          ) : (
            <span className="text-xs text-gray-500">bridge nieosiągalny</span>
          )}
        </div>

        {connected !== undefined && (
          <div
            className={`p-3 rounded ${
              connected
                ? 'bg-green-100 border border-green-300 text-green-800'
                : 'bg-red-100 border border-red-300 text-red-800'
            }`}
          >
            Status: <strong>{connected ? 'Connected' : 'Disconnected'}</strong>
          </div>
        )}

        <div className="flex gap-3">
          <FormButton onClick={handleConnect} loading={loading}>
            Connect
          </FormButton>
          <FormButton onClick={handleStatus} loading={loading}>
            Check Status
          </FormButton>
        </div>

        {error && (
          <JsonResponse success={false} error={error} data={response as any} />
        )}

        {response && !error && (
          <JsonResponse success={connected} data={response as any} />
        )}
      </div>
    </FormLayout>
  )
}
