import React from 'react'

interface FormLayoutProps {
  title: string
  children: React.ReactNode
  onSubmit?: (e: React.FormEvent) => void
}

export function FormLayout({ title, children, onSubmit }: FormLayoutProps) {
  return (
    <div className="max-w-2xl">
      <h1 className="text-2xl font-bold mb-6">{title}</h1>
      <form onSubmit={onSubmit} className="space-y-4">
        {children}
      </form>
    </div>
  )
}

interface FormGroupProps {
  label: string
  children: React.ReactNode
  error?: string
}

export function FormGroup({ label, children, error }: FormGroupProps) {
  return (
    <div className="flex flex-col">
      <label className="font-semibold text-sm mb-2">{label}</label>
      {children}
      {error && <div className="text-red-600 text-sm mt-1">{error}</div>}
    </div>
  )
}

interface FormInputProps
  extends React.InputHTMLAttributes<HTMLInputElement> {}

export function FormInput({ className, ...props }: FormInputProps) {
  return (
    <input
      {...props}
      className={`px-3 py-2 border border-gray-300 rounded focus:outline-none focus:ring-2 focus:ring-blue-500 ${className || ''}`}
    />
  )
}

interface FormSelectProps
  extends React.SelectHTMLAttributes<HTMLSelectElement> {
  options: Array<{ value: string; label: string }>
}

export function FormSelect({
  options,
  className,
  ...props
}: FormSelectProps) {
  return (
    <select
      {...props}
      className={`px-3 py-2 border border-gray-300 rounded focus:outline-none focus:ring-2 focus:ring-blue-500 ${className || ''}`}
    >
      <option value="">Select...</option>
      {options.map((opt) => (
        <option key={opt.value} value={opt.value}>
          {opt.label}
        </option>
      ))}
    </select>
  )
}

interface FormButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  loading?: boolean
}

export function FormButton({ loading, children, ...props }: FormButtonProps) {
  return (
    <button
      {...props}
      disabled={loading || props.disabled}
      className="px-6 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:bg-gray-400 transition-colors"
    >
      {loading ? 'Loading...' : children}
    </button>
  )
}
