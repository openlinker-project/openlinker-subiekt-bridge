import { useState } from 'react'
import { api } from '../lib/apiClient'
import { JsonResponse } from '../components/JsonResponse'
import { FormLayout, FormGroup, FormInput, FormSelect, FormButton } from '../components/FormLayout'

export default function KontrahentUpsertPage() {
  const [formData, setFormData] = useState({
    nazwaSkrocona: '',
    nip: '',
    telefon: '',
    adres: '',
    aktywny: true,
    typ: 'firma',
  })
  const [loading, setLoading] = useState(false)
  const [response, setResponse] = useState<any>(null)
  const [error, setError] = useState<string | undefined>()

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const { name, value, type } = e.target
    setFormData((prev) => ({
      ...prev,
      [name]: type === 'checkbox' ? (e.target as HTMLInputElement).checked : value,
    }))
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResponse(null)
    setError(undefined)

    try {
      const result = await api.upsertKontrahent({
        nazwaSkrocona: formData.nazwaSkrocona,
        nip: formData.nip || undefined,
        telefon: formData.telefon || undefined,
        adres: formData.adres || undefined,
        aktywny: formData.aktywny,
        typ: formData.typ,
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
    <FormLayout title="Upsert Kontrahent" onSubmit={handleSubmit}>
      <FormGroup label="Nazwa Skrócona *">
        <FormInput
          type="text"
          name="nazwaSkrocona"
          value={formData.nazwaSkrocona}
          onChange={handleChange}
          required
        />
      </FormGroup>

      <FormGroup label="NIP">
        <FormInput
          type="text"
          name="nip"
          value={formData.nip}
          onChange={handleChange}
          placeholder="e.g., 1234567890"
        />
      </FormGroup>

      <FormGroup label="Telefon">
        <FormInput
          type="tel"
          name="telefon"
          value={formData.telefon}
          onChange={handleChange}
          placeholder="+48 123456789"
        />
      </FormGroup>

      <FormGroup label="Adres">
        <FormInput
          type="text"
          name="adres"
          value={formData.adres || ''}
          onChange={handleChange}
          placeholder="ul. Testowa 1, 00-000 Warszawa"
        />
      </FormGroup>

      <FormGroup label="Typ">
        <FormSelect
          name="typ"
          value={formData.typ}
          onChange={handleChange}
          options={[
            { value: 'firma', label: 'Firma' },
            { value: 'osoba', label: 'Osoba Fizyczna' },
          ]}
        />
      </FormGroup>

      <FormGroup label="">
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            name="aktywny"
            checked={formData.aktywny}
            onChange={handleChange}
          />
          Aktywny
        </label>
      </FormGroup>

      <FormButton type="submit" loading={loading}>
        Upsert Kontrahent
      </FormButton>

      {response && <JsonResponse success={!error} data={response} error={error} />}
    </FormLayout>
  )
}
