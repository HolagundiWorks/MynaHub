import { useId } from "react";
import { Select, SelectItem } from "@carbon/react";

/** A Carbon Select used as a table filter. The label stays available to assistive technology. */
export default function FilterSelect({ label, value, onChange, options }: { label: string; value: string; onChange: (v: string) => void; options: [string, string][] }) {
  const id = useId();
  return (
    <div className="ao-filter">
    <Select id={id} labelText={label} hideLabel size="sm" value={value} onChange={(e) => onChange(e.target.value)}>
      {options.map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
    </Select>
    </div>
  );
}
