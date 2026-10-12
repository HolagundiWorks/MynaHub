import { Tag } from "@carbon/react";

/** Status is shown as a Carbon Tag with its text, so it never depends on colour alone. */
type TagType = "red" | "magenta" | "purple" | "blue" | "cyan" | "teal" | "green" | "gray" | "cool-gray" | "warm-gray" | "high-contrast" | "outline";

const MAP: Record<string, { label: string; type: TagType }> = {
  draft: { label: "Draft", type: "gray" },
  issued: { label: "Issued", type: "blue" },
  signed: { label: "Signed", type: "green" },
  posted: { label: "Posted", type: "green" },
  paid: { label: "Paid", type: "green" },
  partial: { label: "Partly paid", type: "purple" },
  overdue: { label: "Overdue", type: "red" },
  unmatched: { label: "Unreconciled", type: "red" },
  matched: { label: "Reconciled", type: "green" },
  ignored: { label: "Ignored", type: "cool-gray" },
  duplicate: { label: "Duplicate", type: "cool-gray" },
  cancelled: { label: "Cancelled", type: "cool-gray" },
  void: { label: "Void", type: "cool-gray" },
  reversed: { label: "Reversed", type: "cool-gray" },
  active: { label: "Active", type: "green" },
  on_hold: { label: "On hold", type: "purple" },
  completed: { label: "Completed", type: "green" },
  archived: { label: "Archived", type: "cool-gray" },
  inactive: { label: "Inactive", type: "cool-gray" },
  open: { label: "Open", type: "blue" },
  closed: { label: "Closed", type: "cool-gray" },
  disabled: { label: "Disabled", type: "cool-gray" },
  payable: { label: "Payable", type: "purple" },
  attention: { label: "Attention", type: "red" },
};

export default function StatusBadge({ status, label }: { status: string; label?: string }) {
  const m = MAP[status] ?? { label: status, type: "gray" as TagType };
  return <Tag type={m.type} size="sm">{label ?? m.label}</Tag>;
}
