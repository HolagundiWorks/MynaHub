import { OverflowMenu, OverflowMenuItem } from "@carbon/react";

export type MenuItem = { label: string; onClick: () => void; danger?: boolean; disabled?: boolean };

/** Secondary row actions in Carbon's overflow menu. */
export default function RowMenu({ items, label = "More actions" }: { items: MenuItem[]; label?: string }) {
  if (items.length === 0) return null;
  return (
    <OverflowMenu size="sm" flipped iconDescription={label} aria-label={label}>
      {items.map((it) => (
        <OverflowMenuItem key={it.label} itemText={it.label} isDelete={it.danger} hasDivider={it.danger} disabled={it.disabled} onClick={it.onClick} />
      ))}
    </OverflowMenu>
  );
}
