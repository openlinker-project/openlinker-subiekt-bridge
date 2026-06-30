import { useState, useEffect } from 'react'
import { api } from '../lib/apiClient'
import { JsonResponse } from '../components/JsonResponse'
import { FormLayout, FormGroup, FormInput, FormSelect, FormButton } from '../components/FormLayout'

export default function FakturaPage() {
  const [formData, setFormData] = useState({
    kontrahentId: '',
    documentType: 'FV',
    currency: 'PLN',
    issueDate: new Date().toISOString().split('T')[0],
  })
  const [lineItems, setLineItems] = useState([{ towarSymbol: '', name: '', ilosc: 1, cenaBrutto: 0, stawkaVAT: '23' }])
  const [kontrahenciOptions, setKontrahenciOptions] = useState<Array<{ value: string; label: string }>>([])
  const [towaryOptions, setTowaryOptions] = useState<Array<{ value: string; label: string }>>([])
  const [loading, setLoading] = useState(false)
  const [response, setResponse] = useState<any>(null)
  const [error, setError] = useState<string | undefined>()

  useEffect(() => {
    const fetchKontrahenci = async () => {
      try {
        const result = await api.kontrahenci(100)
        if (result.success) {
          const items = Array.isArray(result.data)
            ? result.data
            : (result.data as any)?.items || []
          setKontrahenciOptions(
            items.map((k: any) => ({
              value: String(k.Id),
              label: `${k.NazwaSkrocona} (${k.NIP || 'no NIP'})`,
            }))
          )
        }
      } catch (err) {
        console.error('Failed to load kontrahenci', err)
      }
    }
    const fetchTowary = async () => {
      try {
        const result = await api.towary(200)
        if (result.success) {
          const items = Array.isArray(result.data)
            ? result.data
            : (result.data as any)?.items || []
          setTowaryOptions(
            items.map((t: any) => ({
              value: t.Symbol,
              label: `${t.Symbol} — ${t.Nazwa}`,
            }))
          )
        }
      } catch (err) {
        console.error('Failed to load towary', err)
      }
    }
    fetchKontrahenci()
    fetchTowary()
  }, [])

  const handleFormChange = (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const { name, value } = e.target
    setFormData((prev) => ({ ...prev, [name]: value }))
  }

  const handleLineItemChange = (idx: number, field: string, value: string | number) => {
    const updated = [...lineItems]
    updated[idx] = { ...updated[idx], [field]: value }
    setLineItems(updated)
  }

  const handleAddLineItem = () => {
    setLineItems((prev) => [...prev, { towarSymbol: '', name: '', ilosc: 1, cenaBrutto: 0, stawkaVAT: '23' }])
  }

  const handleRemoveLineItem = (idx: number) => {
    setLineItems((prev) => prev.filter((_, i) => i !== idx))
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!formData.kontrahentId) {
      setError('Please select a kontrahent')
      return
    }
    if (lineItems.some((li) => !li.towarSymbol)) {
      setError('All line items must have a towar symbol')
      return
    }

    setLoading(true)
    setResponse(null)
    setError(undefined)

    try {
      const result = await api.createFaktura({
        kontrahentId: parseInt(formData.kontrahentId),
        documentType: formData.documentType,
        currency: formData.currency,
        issueDate: formData.issueDate,
        lines: lineItems,
      })
      setResponse(result)
      if (!result.success) {
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
    <FormLayout title="Utwórz Fakturę / Paragon" onSubmit={handleSubmit}>
      <div className="grid grid-cols-2 gap-4">
        <FormGroup label="Nabywca (Kontrahent) *">
          <FormSelect
            name="kontrahentId"
            value={formData.kontrahentId}
            onChange={handleFormChange}
            options={kontrahenciOptions}
          />
        </FormGroup>

        <FormGroup label="Typ Dokumentu">
          <FormSelect
            name="documentType"
            value={formData.documentType}
            onChange={handleFormChange}
            options={[
              { value: 'FV', label: 'Faktura' },
              { value: 'PA', label: 'Paragon' },
            ]}
          />
        </FormGroup>

        <FormGroup label="Waluta">
          <FormSelect
            name="currency"
            value={formData.currency}
            onChange={handleFormChange}
            options={[
              { value: 'PLN', label: 'PLN' },
              { value: 'EUR', label: 'EUR' },
              { value: 'USD', label: 'USD' },
            ]}
          />
        </FormGroup>

        <FormGroup label="Data Wystawienia">
          <FormInput
            type="date"
            name="issueDate"
            value={formData.issueDate}
            onChange={handleFormChange}
          />
        </FormGroup>
      </div>

      <div className="border-t pt-6 mt-6">
        <h3 className="text-lg font-semibold mb-4">Pozycje</h3>

        {lineItems.map((item, idx) => (
          <div key={idx} className="grid grid-cols-6 gap-2 mb-3">
            <FormGroup label={idx === 0 ? 'Symbol (lub spoza Subiekta)' : ''}>
              <input
                list={`towary-list-${idx}`}
                value={item.towarSymbol}
                onChange={(e) => handleLineItemChange(idx, 'towarSymbol', e.target.value)}
                placeholder="np. PRESTA-SKU-XYZ"
                className="w-full px-3 py-2 border border-gray-300 rounded text-sm"
              />
              <datalist id={`towary-list-${idx}`}>
                {towaryOptions.map((o) => (
                  <option key={o.value} value={o.value}>{o.label}</option>
                ))}
              </datalist>
            </FormGroup>

            <FormGroup label={idx === 0 ? 'Nazwa (gdy spoza Subiekta)' : ''}>
              <FormInput
                type="text"
                value={item.name}
                onChange={(e) => handleLineItemChange(idx, 'name', e.target.value)}
                placeholder="Kubek termiczny Presta"
              />
            </FormGroup>

            <FormGroup label={idx === 0 ? 'Ilość' : ''}>
              <FormInput
                type="number"
                value={item.ilosc}
                onChange={(e) => handleLineItemChange(idx, 'ilosc', parseFloat(e.target.value) || 0)}
                step="0.01"
              />
            </FormGroup>

            <FormGroup label={idx === 0 ? 'Cena Brutto' : ''}>
              <FormInput
                type="number"
                value={item.cenaBrutto}
                onChange={(e) => handleLineItemChange(idx, 'cenaBrutto', parseFloat(e.target.value) || 0)}
                step="0.01"
              />
            </FormGroup>

            <FormGroup label={idx === 0 ? 'Stawka VAT' : ''}>
              <FormSelect
                name={`stawkaVAT-${idx}`}
                value={item.stawkaVAT}
                onChange={(e) => handleLineItemChange(idx, 'stawkaVAT', e.target.value)}
                options={[
                  { value: '23', label: '23%' },
                  { value: '8', label: '8%' },
                  { value: '5', label: '5%' },
                  { value: '0', label: '0%' },
                  { value: 'zw', label: 'Zwolnione (zw)' },
                  { value: 'np', label: 'Nie podlega (np)' },
                ]}
              />
            </FormGroup>

            <FormGroup label={idx === 0 ? 'Akcja' : ''}>
              {lineItems.length > 1 && (
                <button
                  type="button"
                  onClick={() => handleRemoveLineItem(idx)}
                  className="px-3 py-2 bg-red-600 text-white rounded hover:bg-red-700 text-sm"
                >
                  Usuń
                </button>
              )}
            </FormGroup>
          </div>
        ))}

        <button
          type="button"
          onClick={handleAddLineItem}
          className="px-4 py-2 bg-gray-300 text-gray-800 rounded hover:bg-gray-400 text-sm"
        >
          + Dodaj pozycję
        </button>
      </div>

      <FormButton type="submit" loading={loading}>
        Utwórz {formData.documentType === 'FV' ? 'Fakturę' : 'Paragon'}
      </FormButton>

      {error && <JsonResponse success={false} error={error} data={response} />}
      {response && !error && <JsonResponse success={true} data={response} />}
    </FormLayout>
  )
}
