/** Product identity. Company files and the engine stay named ADMS. */
export const PRODUCT = "ADMS";
export const PRODUCT_FULL = "Architects Documents Management System";
export const DEVELOPER = "Human Centric Works";

export function ProductCredit() {
  return <p className="ao-muted">{PRODUCT} is developed by {DEVELOPER}.</p>;
}
