import { useEffect, useState } from "react";
import { useDraftSave, useUnsaved } from "../../app/unsaved";
import { Button, Form, InlineNotification, Select, SelectItem, Stack, TextArea, TextInput } from "@carbon/react";
import { api, errorText, type CompanyProfile } from "../../api";
import PageHeader from "../../components/PageHeader";

const EMPTY: CompanyProfile = { name: "", address: "", email: "", phone: "", pan: "", gstin: "", coa_registration: "", bank_details: "", gst_scheme: "none", upi_id: "", architect_name: "", letterhead_style: "classic" };

export default function CompanyPage() {
  const [profile, setProfile] = useState<CompanyProfile>(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");

  useEffect(() => { api.companyGet().then((p) => { setProfile(p); setLoaded(true); }).catch((e) => setError(errorText(e))); }, []);

  const set = (patch: Partial<CompanyProfile>) => { setProfile({ ...profile, ...patch }); setDirty(true); setOk(""); };
  useUnsaved(dirty);
  const save = async () => {
    if (saving || !dirty) return;
    setSaving(true);
    try { await api.companySave(profile); setOk("Saved."); setDirty(false); setError(""); } catch (err) { setError(errorText(err)); setOk(""); }
    setSaving(false);
  };
  useDraftSave(loaded ? () => { void save(); } : null);
  const field = (key: keyof CompanyProfile, label: string, extra?: { placeholder?: string }) => (
    <TextInput size="sm" id={key} labelText={label} placeholder={extra?.placeholder} value={profile[key]} onChange={(e) => set({ [key]: e.target.value } as Partial<CompanyProfile>)} />
  );

  return (
    <>
      <PageHeader title="Company profile" description="Used on every invoice, letter and agreement. Changing it does not alter documents already issued." />
      {!loaded && !error && <p>Loading…</p>}
      {error && <InlineNotification kind="error" title="Could not load or save the profile" subtitle={error} hideCloseButton lowContrast />}
      {loaded && (
        <Form className="ao-form" onSubmit={async (e) => { e.preventDefault(); await save(); }}>
          <Stack gap={5}>
            {field("name", "Firm name")}
            {field("architect_name", "Architect name (signature block)")}
            <TextArea id="address" labelText="Address" rows={3} value={profile.address} onChange={(e) => set({ address: e.target.value })} />
            <Select size="sm" id="letterhead_style" labelText="Letterhead style" value={profile.letterhead_style} helperText="How your details are set at the top of letters, agreements and records."
              onChange={(e) => set({ letterhead_style: e.target.value as CompanyProfile["letterhead_style"] })}>
              <SelectItem value="classic" text="Classic: firm name large, details beneath" />
              <SelectItem value="minimal" text="Minimal: small name line and one line of details" />
            </Select>
            {field("email", "Email")}
            {field("phone", "Phone")}
            {field("pan", "PAN")}
            {field("coa_registration", "COA registration number")}
            <Select size="sm" id="gst_scheme" labelText="GST registration" value={profile.gst_scheme} helperText="Controls the tax on every new invoice."
              onChange={(e) => set({ gst_scheme: e.target.value as CompanyProfile["gst_scheme"] })}>
              <SelectItem value="none" text="Not registered / no GST" />
              <SelectItem value="composition" text="Composition scheme (6% = 3% CGST + 3% SGST, paid by the firm)" />
              <SelectItem value="regular" text="Regular (18% = 9% CGST + 9% SGST, or 18% IGST)" />
            </Select>
            {field("gstin", "GSTIN (if registered)")}
            {field("upi_id", "UPI ID (printed as a QR code on invoices)", { placeholder: "name@bank" })}
            <TextArea id="bank_details" labelText="Bank details used when no bank account is set up" rows={3} value={profile.bank_details} onChange={(e) => set({ bank_details: e.target.value })} />
            {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
            <div className="toolbar">
              <span>{dirty ? "Unsaved changes" : ""}</span>
              <Button size="sm" type="submit" disabled={saving || !dirty}>{saving ? "Saving…" : "Save"}</Button>
            </div>
          </Stack>
        </Form>
      )}
    </>
  );
}
