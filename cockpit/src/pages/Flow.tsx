import { useState } from 'react'
import { api } from '../lib/apiClient'
import { JsonResponse } from '../components/JsonResponse'
import { FormLayout, FormGroup, FormInput, FormButton } from '../components/FormLayout'

export default function FlowPage() {
  const [step, setStep] = useState<'idle' | 'running' | 'done'>('idle')
  const [formData, setFormData] = useState({
    nazwaSkrocona: '',
    nip: '',
    symbol: 'SKU001',
    ilosc: 1,
    cena: 100,
  })

  const [results, setResults] = useState({
    kontrahent: null as any,
    faktura: null as any,
    status: null as any,
  })

  const [errors, setErrors] = useState({
    kontrahent: '',
    faktura: '',
    status: '',
  })

  const handleFormChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const { name, value } = e.target
    setFormData((prev) => ({
      ...prev,
      [name]:
        name === 'ilosc' || name === 'cena'
          ? parseFloat(value) || 0
          : value,
    }))
  }

  const handleExecuteFlow = async () => {
    setStep('running')
    setResults({ kontrahent: null, faktura: null, status: null })
    setErrors({ kontrahent: '', faktura: '', status: '' })

    try {
      // Step 1: Upsert kontrahent
      const upsertRes = await api.upsertKontrahent({
        nazwaSkrocona: formData.nazwaSkrocona,
        nip: formData.nip || undefined,
        aktywny: true,
        typ: 'firma',
      })

      if (!upsertRes.success) {
        setErrors((prev) => ({ ...prev, kontrahent: upsertRes.error || '' }))
        setResults((prev) => ({ ...prev, kontrahent: upsertRes }))
        setStep('done')
        return
      }

      const kontrahentId = (upsertRes.data as any).id
      setResults((prev) => ({ ...prev, kontrahent: upsertRes }))

      // Step 2: Create faktura
      const fakturaRes = await api.createFaktura({
        kontrahentId,
        documentType: 'FV',
        currency: 'PLN',
        issueDate: new Date().toISOString().split('T')[0],
        lines: [
          {
            towarSymbol: formData.symbol,
            ilosc: formData.ilosc,
            cenaBrutto: formData.cena,
            stawkaVAT: '23',
          },
        ],
      })

      if (!fakturaRes.success) {
        setErrors((prev) => ({ ...prev, faktura: fakturaRes.error || '' }))
        setResults((prev) => ({ ...prev, faktura: fakturaRes }))
        setStep('done')
        return
      }

      const invoiceId = (fakturaRes.data as any).id ?? (fakturaRes.data as any).providerInvoiceId
      setResults((prev) => ({ ...prev, faktura: fakturaRes }))

      // Step 3: Get invoice status
      const statusRes = await api.invoiceStatus(String(invoiceId))

      if (!statusRes.success) {
        setErrors((prev) => ({ ...prev, status: statusRes.error || '' }))
        setResults((prev) => ({ ...prev, status: statusRes }))
        setStep('done')
        return
      }

      setResults((prev) => ({ ...prev, status: statusRes }))
      setStep('done')
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      setErrors({ kontrahent: msg, faktura: '', status: '' })
      setStep('done')
    }
  }

  return (
    <FormLayout title="E2E Flow: Upsert → Faktura → Status">
      <div className="space-y-6">
        {/* Input form */}
        <div className="bg-gray-50 p-6 rounded">
          <h2 className="text-lg font-semibold mb-4">Workflow Input</h2>

          <div className="grid grid-cols-2 gap-4">
            <FormGroup label="Nazwa Skrócona *">
              <FormInput
                type="text"
                name="nazwaSkrocona"
                value={formData.nazwaSkrocona}
                onChange={handleFormChange}
                disabled={step === 'running'}
              />
            </FormGroup>

            <FormGroup label="NIP">
              <FormInput
                type="text"
                name="nip"
                value={formData.nip}
                onChange={handleFormChange}
                disabled={step === 'running'}
              />
            </FormGroup>

            <FormGroup label="Symbol Towaru">
              <FormInput
                type="text"
                name="symbol"
                value={formData.symbol}
                onChange={handleFormChange}
                disabled={step === 'running'}
              />
            </FormGroup>

            <FormGroup label="Ilość">
              <FormInput
                type="number"
                name="ilosc"
                value={formData.ilosc}
                onChange={handleFormChange}
                disabled={step === 'running'}
                step="0.01"
              />
            </FormGroup>

            <FormGroup label="Cena">
              <FormInput
                type="number"
                name="cena"
                value={formData.cena}
                onChange={handleFormChange}
                disabled={step === 'running'}
                step="0.01"
              />
            </FormGroup>
          </div>

          <FormButton
            type="button"
            onClick={handleExecuteFlow}
            loading={step === 'running'}
            disabled={step === 'running'}
            style={{ marginTop: '1rem' }}
          >
            Execute Flow
          </FormButton>
        </div>

        {/* Results */}
        {step === 'done' && (
          <div className="space-y-4">
            <h2 className="text-lg font-semibold">Results</h2>

            <div>
              <h3 className="font-semibold mb-2">Step 1: Upsert Kontrahent</h3>
              <JsonResponse
                success={!errors.kontrahent}
                data={results.kontrahent as any}
                error={errors.kontrahent}
              />
            </div>

            {results.faktura && (
              <div>
                <h3 className="font-semibold mb-2">Step 2: Create Faktura</h3>
                <JsonResponse
                  success={!errors.faktura}
                  data={results.faktura as any}
                  error={errors.faktura}
                />
              </div>
            )}

            {results.status && (
              <div>
                <h3 className="font-semibold mb-2">Step 3: Invoice Status</h3>
                <JsonResponse
                  success={!errors.status}
                  data={results.status as any}
                  error={errors.status}
                />
              </div>
            )}
          </div>
        )}
      </div>
    </FormLayout>
  )
}
