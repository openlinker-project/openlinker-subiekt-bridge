import { useState, useEffect, useRef } from 'react'
import { api } from '../lib/apiClient'
import { JsonResponse } from '../components/JsonResponse'
import { FormLayout, FormGroup, FormInput, FormButton } from '../components/FormLayout'

interface StatusObservation {
  at: string
  regulatoryStatus: string
  clearanceReference?: string | null
}

const ksefColor = (s?: string) =>
  s === 'accepted' ? 'bg-green-100 text-green-800'
  : s === 'sent' ? 'bg-blue-100 text-blue-800'
  : s === 'pending' ? 'bg-yellow-100 text-yellow-800'
  : s === 'rejected' ? 'bg-red-100 text-red-800'
  : 'bg-gray-100 text-gray-700'

export default function KsefStatusPage() {
  const [invoiceId, setInvoiceId] = useState('')
  const [response, setResponse] = useState<any>(null)
  const [error, setError] = useState<string | undefined>()
  const [loading, setLoading] = useState(false)
  const [polling, setPolling] = useState(false)
  const [history, setHistory] = useState<StatusObservation[]>([])
  const timer = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchStatus = async (id: string) => {
    const result = await api.invoiceStatus(id)
    setResponse(result)
    setError(result.success ? undefined : result.error)
    const reg = (result.data as any)?.regulatoryStatus ?? (result.data as any)?.ksef?.status
    const ref = (result.data as any)?.clearanceReference
    if (result.success && reg) {
      setHistory((prev) => {
        // only append when the observed status actually changes
        if (prev.length && prev[prev.length - 1].regulatoryStatus === reg) return prev
        return [...prev, { at: new Date().toLocaleTimeString(), regulatoryStatus: reg, clearanceReference: ref }]
      })
    }
    return reg as string | undefined
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!invoiceId.trim()) {
      setError('Please enter an invoice ID')
      return
    }
    setLoading(true)
    setResponse(null)
    setError(undefined)
    setHistory([])
    try {
      await fetchStatus(invoiceId.trim())
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      setError(msg)
      setResponse({ error: msg })
    } finally {
      setLoading(false)
    }
  }

  // Poll every 3s while enabled — shows KSeF state transitions over time.
  useEffect(() => {
    if (polling && invoiceId.trim()) {
      timer.current = setInterval(() => {
        fetchStatus(invoiceId.trim()).then((reg) => {
          // auto-stop once a terminal state is reached
          if (reg === 'accepted' || reg === 'rejected' || reg === 'none') setPolling(false)
        })
      }, 3000)
    }
    return () => {
      if (timer.current) clearInterval(timer.current)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polling, invoiceId])

  const latest = history.length ? history[history.length - 1].regulatoryStatus : undefined

  return (
    <FormLayout title="KSeF / Invoice Status" onSubmit={handleSubmit}>
      <FormGroup label="Invoice ID (providerInvoiceId) *">
        <FormInput
          type="text"
          value={invoiceId}
          onChange={(e) => setInvoiceId(e.target.value)}
          placeholder="np. 42"
          required
        />
      </FormGroup>

      <div className="flex gap-3 items-center">
        <FormButton type="submit" loading={loading}>
          Pobierz status
        </FormButton>
        <button
          type="button"
          onClick={() => setPolling((p) => !p)}
          className={`px-4 py-2 rounded text-sm font-medium ${
            polling ? 'bg-red-600 text-white hover:bg-red-700' : 'bg-blue-600 text-white hover:bg-blue-700'
          }`}
        >
          {polling ? 'Zatrzymaj polling' : 'Polluj co 3s'}
        </button>
        {latest && (
          <span className={`px-3 py-1 rounded text-sm font-bold ${ksefColor(latest)}`}>
            KSeF: {latest}
          </span>
        )}
      </div>

      {history.length > 0 && (
        <div className="mt-4 border rounded p-3 bg-gray-50">
          <h4 className="text-sm font-semibold mb-2">Przejścia statusu KSeF</h4>
          <ul className="text-xs space-y-1">
            {history.map((h, i) => (
              <li key={i} className="flex gap-2 items-center">
                <span className="text-gray-500">{h.at}</span>
                <span className={`px-2 py-0.5 rounded ${ksefColor(h.regulatoryStatus)}`}>{h.regulatoryStatus}</span>
                {h.clearanceReference && <span className="text-gray-600">KSEF_ID: {h.clearanceReference}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}

      {response && <JsonResponse success={!error} data={response as any} error={error} />}
    </FormLayout>
  )
}
