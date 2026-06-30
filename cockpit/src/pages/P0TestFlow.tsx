import { useState } from 'react'
import { api } from '../lib/apiClient'
import { FormLayout, FormButton } from '../components/FormLayout'
import { randomValidNip } from '../lib/nip'

interface TestStep {
  name: string
  status: 'pending' | 'running' | 'success' | 'error'
  result?: any
  error?: string
}

export default function P0TestFlowPage() {
  const [steps, setSteps] = useState<TestStep[]>([
    { name: 'Connect to Sfera', status: 'pending' },
    { name: 'Upsert Kontrahent #1 (Idempotency Test)', status: 'pending' },
    { name: 'Upsert Kontrahent #2 (Same NIP - Should Return Same ID)', status: 'pending' },
    { name: 'Create Invoice FV/2024 (Single Line Item)', status: 'pending' },
    { name: 'Create Invoice FV/2025 (Multiple VAT Rates)', status: 'pending' },
    { name: 'Get Invoice Status', status: 'pending' },
    { name: 'Verify Audit Log', status: 'pending' },
  ])
  const [running, setRunning] = useState(false)

  const updateStep = (idx: number, updates: Partial<TestStep>) => {
    setSteps((prev) => {
      const updated = [...prev]
      updated[idx] = { ...updated[idx], ...updates }
      return updated
    })
  }

  const runP0Tests = async () => {
    setRunning(true)

    // Test 1: Connect
    updateStep(0, { status: 'running' })
    try {
      const connectResult = await api.connect()
      updateStep(0, { status: connectResult.success ? 'success' : 'error', result: connectResult })
      if (!connectResult.success) {
        setRunning(false)
        return
      }
    } catch (err) {
      updateStep(0, { status: 'error', error: String(err) })
      setRunning(false)
      return
    }

    // Test 2: Upsert Kontrahent #1
    const testNIP = randomValidNip()
    // Local var avoids reading stale React state inside this async function.
    let firstUpsertId = 0
    updateStep(1, { status: 'running' })
    try {
      const result = await api.upsertKontrahent({
        nazwaSkrocona: 'P0 Test Firma 1',
        nip: testNIP,
        telefon: '123456789',
        adres: 'ul. Testowa 1, 00-000 Warszawa',
        aktywny: true,
        typ: 'firma',
      })
      firstUpsertId = (result as any)?.data?.id
      updateStep(1, { status: result.success ? 'success' : 'error', result })
      if (!result.success) {
        setRunning(false)
        return
      }
    } catch (err) {
      updateStep(1, { status: 'error', error: String(err) })
      setRunning(false)
      return
    }

    // Test 3: Upsert Same NIP (Idempotency)
    updateStep(2, { status: 'running' })
    try {
      const result = await api.upsertKontrahent({
        nazwaSkrocona: 'P0 Test Firma 1',
        nip: testNIP,
        telefon: '123456789',
        aktywny: true,
        typ: 'firma',
      })
      const secondId = (result as any)?.data?.id
      const isSameId = firstUpsertId === secondId
      updateStep(2, {
        status: result.success && isSameId ? 'success' : 'error',
        result: { ...result, idempotencyCheck: isSameId, firstId: firstUpsertId, secondId },
      })
      if (!result.success || !isSameId) {
        setRunning(false)
        return
      }
    } catch (err) {
      updateStep(2, { status: 'error', error: String(err) })
      setRunning(false)
      return
    }

    const kontrahentId = firstUpsertId || 0

    // Fetch REAL product symbols from the DB — fake "PROD-001" symbols don't exist.
    let realSymbols: string[] = []
    try {
      const tow = await api.towary(10)
      const items = Array.isArray(tow.data) ? tow.data : (tow.data as any)?.items || []
      realSymbols = items.map((t: any) => t.Symbol).filter(Boolean)
    } catch {
      // ignore — handled below
    }
    if (realSymbols.length === 0) {
      updateStep(3, { status: 'error', error: 'No products found in DB (GET /api/products returned none)' })
      setRunning(false)
      return
    }
    const sym = (i: number) => realSymbols[i % realSymbols.length]

    // Test 4: Create Invoice with Single Line
    let firstInvoiceId = 0
    updateStep(3, { status: 'running' })
    try {
      const result = await api.createFaktura({
        kontrahentId: kontrahentId,
        documentType: 'FV',
        currency: 'PLN',
        issueDate: new Date().toISOString().split('T')[0],
        lines: [
          {
            towarSymbol: sym(0),
            ilosc: 1,
            cenaBrutto: 100,
            stawkaVAT: '23',
          },
        ],
      })
      firstInvoiceId = (result as any)?.data?.id
      updateStep(3, { status: result.success ? 'success' : 'error', result })
      if (!result.success) {
        setRunning(false)
        return
      }
    } catch (err) {
      updateStep(3, { status: 'error', error: String(err) })
      setRunning(false)
      return
    }

    // Test 5: Create Invoice with Multiple VAT Rates
    updateStep(4, { status: 'running' })
    try {
      const result = await api.createFaktura({
        kontrahentId: kontrahentId,
        documentType: 'FV',
        currency: 'PLN',
        issueDate: new Date().toISOString().split('T')[0],
        lines: [
          { towarSymbol: sym(0), ilosc: 1, cenaBrutto: 100, stawkaVAT: '23' },
          { towarSymbol: sym(1), ilosc: 1, cenaBrutto: 50, stawkaVAT: '8' },
          { towarSymbol: sym(2), ilosc: 1, cenaBrutto: 25, stawkaVAT: '0' },
        ],
      })
      updateStep(4, { status: result.success ? 'success' : 'error', result })
      if (!result.success) {
        setRunning(false)
        return
      }
    } catch (err) {
      updateStep(4, { status: 'error', error: String(err) })
      setRunning(false)
      return
    }

    const invoiceId = firstInvoiceId

    // Test 6: Get Invoice Status
    if (invoiceId) {
      updateStep(5, { status: 'running' })
      try {
        const result = await api.invoiceStatus(String(invoiceId))
        updateStep(5, { status: result.success ? 'success' : 'error', result })
      } catch (err) {
        updateStep(5, { status: 'error', error: String(err) })
      }
    }

    // Test 7: Get Audit Log
    updateStep(6, { status: 'running' })
    try {
      const result = await api.auditLast(10)
      updateStep(6, { status: result.success ? 'success' : 'error', result })
    } catch (err) {
      updateStep(6, { status: 'error', error: String(err) })
    }

    setRunning(false)
  }

  return (
    <FormLayout title="P0 Test Flow">
      <div className="mb-6">
        <p className="text-sm text-gray-600 mb-4">
          Automated test suite for P0: Real write operations, idempotency, and audit logging.
        </p>
        <FormButton onClick={runP0Tests} loading={running} disabled={running}>
          {running ? 'Running Tests...' : 'Run All P0 Tests'}
        </FormButton>
      </div>

      <div className="space-y-3">
        {steps.map((step, idx) => (
          <div
            key={idx}
            className={`border-l-4 p-4 rounded ${
              step.status === 'success'
                ? 'border-green-500 bg-green-50'
                : step.status === 'error'
                  ? 'border-red-500 bg-red-50'
                  : step.status === 'running'
                    ? 'border-blue-500 bg-blue-50'
                    : 'border-gray-300 bg-gray-50'
            }`}
          >
            <div className="flex items-center justify-between mb-2">
              <h4 className="font-semibold">{step.name}</h4>
              <span
                className={`text-xs font-bold px-2 py-1 rounded ${
                  step.status === 'success'
                    ? 'bg-green-500 text-white'
                    : step.status === 'error'
                      ? 'bg-red-500 text-white'
                      : step.status === 'running'
                        ? 'bg-blue-500 text-white'
                        : 'bg-gray-400 text-white'
                }`}
              >
                {step.status.toUpperCase()}
              </span>
            </div>

            {step.error && <p className="text-red-600 text-sm mb-2">Error: {step.error}</p>}

            {step.result && (
              <details className="text-sm">
                <summary className="cursor-pointer text-gray-600 hover:text-gray-800">
                  View Response
                </summary>
                <pre className="bg-gray-800 text-green-400 p-3 rounded mt-2 overflow-auto max-h-48 text-xs">
                  {JSON.stringify(step.result, null, 2)}
                </pre>
              </details>
            )}
          </div>
        ))}
      </div>
    </FormLayout>
  )
}
