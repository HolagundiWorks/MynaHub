//! Writes a sample project file for trying the readers: `cargo run --example sample -- <path>`.
use shilpi_project::*;

fn main() {
    let path = std::env::args().nth(1).expect("give the file to write");
    let s = |v: &str| v.to_string();
    let data = ProjectData {
        estimate: vec![
            EstimateItem { code: s("A1"), description: s("Excavation in ordinary soil"), unit: s("cum"), quantity: 420.0, rate: 310.0 },
            EstimateItem { code: s("B2"), description: s("RCC M25 footings and columns"), unit: s("cum"), quantity: 96.5, rate: 9200.0 },
            EstimateItem { code: s("C3"), description: s("230 mm brick masonry"), unit: s("cum"), quantity: 210.0, rate: 6400.0 },
        ],
        billing: vec![
            BillingStage { stage: s("Concept design"), description: s("Plans and massing"), percent: 15.0, amount: 150000.0, status: s("paid"), date: s("2026-08-12") },
            BillingStage { stage: s("Working drawings"), description: s("Floors 1 to 3"), percent: 40.0, amount: 400000.0, status: s("invoiced"), date: s("2026-10-05") },
            BillingStage { stage: s("Site supervision"), description: s("Monthly visits"), percent: 25.0, amount: 250000.0, status: s("pending"), date: s("") },
        ],
        tasks: vec![
            Task { title: s("Issue structural drawings"), assignee: s("Meena Rao"), due: s("2026-11-05"), status: s("doing"), percent: 60.0 },
            Task { title: s("Coordinate services layout"), assignee: s("Arjun Shetty"), due: s("2026-11-12"), status: s("open"), percent: 0.0 },
            Task { title: s("Site measurements"), assignee: s("Divya Nair"), due: s("2026-10-20"), status: s("done"), percent: 100.0 },
        ],
        quantities: vec![
            Quantity { item: s("Walls 230 mm"), description: s("Ground floor"), unit: s("sqm"), quantity: 312.4, source: s("layer A-WALL") },
            Quantity { item: s("Doors"), description: s("All floors"), unit: s("nos"), quantity: 38.0, source: s("blocks D*") },
            Quantity { item: s("Slab area"), description: s("Typical floor"), unit: s("sqm"), quantity: 186.0, source: s("layer S-SLAB") },
        ],
        orders: vec![
            PurchaseOrder { number: s("PO-0012"), vendor: s("Mysuru Steel Traders"), date: s("2026-10-02"), description: s("TMT bars Fe500D"), amount: 842000.0, status: s("issued") },
            PurchaseOrder { number: s("PO-0013"), vendor: s("Kaveri Cement Depot"), date: s("2026-10-09"), description: s("OPC 53 cement, 600 bags"), amount: 234000.0, status: s("received") },
        ],
        other_records: 0,
    };
    write_file(std::path::Path::new(&path), &data).expect("write");
    println!("wrote {path}");
}
