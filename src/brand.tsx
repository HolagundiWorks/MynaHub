/** Product identity. Company files stay .adms. */
export const PRODUCT = "AOS";
export const PRODUCT_FULL = "Architecture Office Suite";
export const DEVELOPER = "Human Centric Works";

export function ProductCredit() {
  return <p className="ao-muted">{PRODUCT} is developed by {DEVELOPER}.</p>;
}
