using MIGAZ.Models;
using MIGAZ.Models.ARM;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace MIGAZ.Generator
{
    public class TemplateGenerator
    {
        private ILogProvider _logProvider;
        private IStatusProvider _statusProvider;
        private ITelemetryProvider _telemetryProvider;
        private ITokenProvider _tokenProvider;
        private AsmRetriever _asmRetriever;
        private List<Resource> _resources;
        private Dictionary<string, Parameter> _parameters;
        private List<CopyBlobDetail> _copyBlobDetails;
        private Dictionary<string, string> _processedItems;

        public TemplateGenerator(ILogProvider logProvider, IStatusProvider statusProvider, ITelemetryProvider telemetryProvider, ITokenProvider tokenProvider, AsmRetriever asmRetriever)
        {
            _logProvider = logProvider;
            _statusProvider = statusProvider;
            _telemetryProvider = telemetryProvider;
            _tokenProvider = tokenProvider;
            _asmRetriever = asmRetriever;
        }
        public void GenerateTemplate(string tenantId, string subscriptionId, AsmArtefacts artefacts, StreamWriter templateWriter, StreamWriter blobDetailWriter)
        {
            _logProvider.WriteLog("GenerateTemplate", "Start");

            app.Default.ExecutionId = Guid.NewGuid().ToString();
            _resources = new List<Resource>();
            _parameters = new Dictionary<string, Parameter>();

            _processedItems = new Dictionary<string, string>();
            _copyBlobDetails = new List<CopyBlobDetail>();

            var token = _tokenProvider.GetToken(tenantId);

            _logProvider.WriteLog("GenerateTemplate", "Start processing selected virtual networks");
            // process selected virtual networks
            foreach (var virtualnetworkname in artefacts.VirtualNetworks)
            {
                _statusProvider.UpdateStatus("BUSY: Exporting Virtual Network : " + virtualnetworkname);

                foreach (XmlNode virtualnetworksite in _asmRetriever.GetAzureASMResources("VirtualNetworks", subscriptionId, null, token).SelectNodes("//VirtualNetworkSite"))
                {
                    if (virtualnetworksite.SelectSingleNode("Name").InnerText == virtualnetworkname)
                    {
                        BuildVirtualNetworkObject(subscriptionId, virtualnetworksite, token);
                    }
                }
            }
            _logProvider.WriteLog("GenerateTemplate", "End processing selected virtual networks");

            _logProvider.WriteLog("GenerateTemplate", "Start processing selected storage accounts");

            // process selected storage accounts
            foreach (var storageaccountname in artefacts.StorageAccounts)
            {
                _statusProvider.UpdateStatus("BUSY: Exporting Storage Account : " + storageaccountname);

                Hashtable storageaccountinfo = new Hashtable();
                storageaccountinfo.Add("name", storageaccountname);

                XmlNode storageaccount = _asmRetriever.GetAzureASMResources("StorageAccount", subscriptionId, storageaccountinfo, token);
                BuildStorageAccountObject(storageaccount);
            }
            _logProvider.WriteLog("GenerateTemplate", "End processing selected storage accounts");

            _logProvider.WriteLog("GenerateTemplate", "Start processing selected cloud services and virtual machines");

            // process selected cloud services and virtual machines
            foreach (var cloudServiceVM in artefacts.VirtualMachines)
            {
                string deploymentName;
                string virtualNetworkName;
                string loadBalancerName;
                _asmRetriever.GetVMDetails(subscriptionId, cloudServiceVM.CloudService, cloudServiceVM.VirtualMachine, token, out deploymentName, out virtualNetworkName, out loadBalancerName);

                Hashtable cloudserviceinfo = new Hashtable();
                cloudserviceinfo.Add("name", cloudServiceVM.CloudService);
                XmlDocument cloudservice = _asmRetriever.GetAzureASMResources("CloudService", subscriptionId, cloudserviceinfo, token);
                string location = cloudservice.SelectSingleNode("//HostedServiceProperties/ExtendedProperties/ExtendedProperty[Name='ResourceLocation']/Value").InnerText;


                _statusProvider.UpdateStatus("BUSY: Exporting Cloud Service : " + cloudServiceVM.CloudService);

                XmlDocument reservedips = _asmRetriever.GetAzureASMResources("ReservedIPs", subscriptionId, null, token);
                BuildPublicIPAddressObject(cloudservice, loadBalancerName, cloudServiceVM.CloudService, reservedips);
                BuildLoadBalancerObject(subscriptionId, cloudservice, loadBalancerName, artefacts, token);
        
                Hashtable virtualmachineinfo = new Hashtable();
                virtualmachineinfo.Add("cloudservicename", cloudServiceVM.CloudService);
                virtualmachineinfo.Add("deploymentname", deploymentName);
                virtualmachineinfo.Add("virtualmachinename", cloudServiceVM.VirtualMachine);
                virtualmachineinfo.Add("virtualnetworkname", virtualNetworkName);
                virtualmachineinfo.Add("loadbalancername", loadBalancerName);
                virtualmachineinfo.Add("location", location);

                XmlDocument virtualmachine = _asmRetriever.GetAzureASMResources("VirtualMachine", subscriptionId, virtualmachineinfo, token);

                // create new virtual network if virtualnetworkname is "empty"
                if (virtualNetworkName == "empty")
                {
                    BuildNewVirtualNetworkObject(cloudservice, virtualmachineinfo);
                }

                // process availability set
                BuildAvailabilitySetObject(virtualmachine, virtualmachineinfo);

                // process network interface
                List<NetworkProfile_NetworkInterface> networkinterfaces = new List<NetworkProfile_NetworkInterface>();
                BuildNetworkInterfaceObject(subscriptionId, virtualmachine, virtualmachineinfo, ref networkinterfaces, token);

                // process virtual machine
                BuildVirtualMachineObject(subscriptionId, virtualmachine, virtualmachineinfo, networkinterfaces, token);
            }
            _logProvider.WriteLog("GenerateTemplate", "End processing selected cloud services and virtual machines");

            Template template = new Template();
            template.resources = _resources;
            template.parameters = _parameters;

            // save JSON template
            _statusProvider.UpdateStatus("BUSY: saving JSON template");
            string jsontext = JsonConvert.SerializeObject(template, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            jsontext = jsontext.Replace("schemalink", "$schema");
            WriteStream(templateWriter, jsontext);
            _logProvider.WriteLog("GenerateTemplate", "Write file export.json");

            // save blob copy details file
            _statusProvider.UpdateStatus("BUSY: saving blob copy details file");
            jsontext = JsonConvert.SerializeObject(_copyBlobDetails, Newtonsoft.Json.Formatting.Indented, new JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            WriteStream(blobDetailWriter, jsontext);
            _logProvider.WriteLog("GenerateTemplate", "Write file copyblobdetails.json");

            // post Telemetry Record to ASMtoARMToolAPI
            if (app.Default.AllowTelemetry)
            {
                XmlDocument subscriptions = _asmRetriever.GetAzureASMResources("Subscriptions", subscriptionId, null, token);
                string offercategories = "";
                foreach (XmlNode subscription in subscriptions.SelectNodes("/Subscriptions/Subscription"))
                {
                    if (subscription.SelectSingleNode("SubscriptionID").InnerText == subscriptionId)
                    {
                        offercategories = subscription.SelectSingleNode("OfferCategories").InnerText;
                    }
                }

                _statusProvider.UpdateStatus("BUSY: saving telemetry information");
                _telemetryProvider.PostTelemetryRecord(tenantId, subscriptionId, _processedItems, offercategories);
            }

            _statusProvider.UpdateStatus("Ready");

            _logProvider.WriteLog("GenerateTemplate", "End");
        }

        private void BuildPublicIPAddressObject(ref NetworkInterface networkinterface)
        {
            _logProvider.WriteLog("BuildPublicIPAddressObject", "Start");

            PublicIPAddress publicipaddress = new PublicIPAddress();
            publicipaddress.name = networkinterface.name;
            publicipaddress.location = networkinterface.location;
            publicipaddress.properties = new PublicIPAddress_Properties();

            try // it fails if this public ip address was already processed. safe to continue.
            {
                _processedItems.Add("Microsoft.Network/publicIPAddresses/" + publicipaddress.name, publicipaddress.location);
                _resources.Add(publicipaddress);
                _logProvider.WriteLog("BuildPublicIPAddressObject", "Microsoft.Network/publicIPAddresses/" + publicipaddress.name);
            }
            catch { }

            NetworkInterface_Properties networkinterface_properties = (NetworkInterface_Properties)networkinterface.properties;
            networkinterface_properties.ipConfigurations[0].properties.publicIPAddress = new Reference();
            networkinterface_properties.ipConfigurations[0].properties.publicIPAddress.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/publicIPAddresses/" + publicipaddress.name + "')]";
            networkinterface.properties = networkinterface_properties;

            networkinterface.dependsOn.Add(networkinterface_properties.ipConfigurations[0].properties.publicIPAddress.id);
            _logProvider.WriteLog("BuildPublicIPAddressObject", "End");
        }

        private void BuildPublicIPAddressObject(XmlNode resource, string loadbalancername, string cloudservicename, XmlDocument reservedips)
        {
            _logProvider.WriteLog("BuildPublicIPAddressObject", "Start");

            string publicipaddress_name = loadbalancername;

            string publicipallocationmethod = "Dynamic";
            foreach (XmlNode reservedip in reservedips.SelectNodes("/ReservedIPs/ReservedIP"))
            {
                if (reservedip.SelectNodes("ServiceName").Count > 0)
                {
                    if (reservedip.SelectSingleNode("ServiceName").InnerText == cloudservicename)
                    {
                        publicipallocationmethod = "Static";
                    }
                }
            }

            Hashtable dnssettings = new Hashtable();
            dnssettings.Add("domainNameLabel", (publicipaddress_name + app.Default.UniquenessSuffix).ToLower());

            PublicIPAddress_Properties publicipaddress_properties = new PublicIPAddress_Properties();
            publicipaddress_properties.dnsSettings = dnssettings;
            publicipaddress_properties.publicIPAllocationMethod = publicipallocationmethod;

            PublicIPAddress publicipaddress = new PublicIPAddress();
            publicipaddress.name = publicipaddress_name + "-PIP";
            publicipaddress.location = resource.SelectSingleNode("//HostedServiceProperties/ExtendedProperties/ExtendedProperty[Name='ResourceLocation']/Value").InnerText;
            publicipaddress.properties = publicipaddress_properties;

            try // it fails if this public ip address was already processed. safe to continue.
            {
                _processedItems.Add("Microsoft.Network/publicIPAddresses/" + publicipaddress.name, publicipaddress.location);
                _resources.Add(publicipaddress);
                _logProvider.WriteLog("BuildPublicIPAddressObject", "Microsoft.Network/publicIPAddresses/" + publicipaddress.name);
            }
            catch { }

            _logProvider.WriteLog("BuildPublicIPAddressObject", "End");
        }

        private void BuildAvailabilitySetObject(XmlNode virtualmachine, Hashtable virtualmachineinfo)
        {
            _logProvider.WriteLog("BuildAvailabilitySetObject", "Start");

            string virtualmachinename = virtualmachineinfo["virtualmachinename"].ToString();
            string cloudservicename = virtualmachineinfo["cloudservicename"].ToString();

            AvailabilitySet availabilityset = new AvailabilitySet();

            availabilityset.name = cloudservicename + "-defaultAS";
            if (virtualmachine.SelectSingleNode("//AvailabilitySetName") != null)
            {
                availabilityset.name = virtualmachine.SelectSingleNode("//AvailabilitySetName").InnerText;
            }
            availabilityset.location = virtualmachineinfo["location"].ToString();
            try // it fails if this availability set was already processed. safe to continue.
            {
                _processedItems.Add("Microsoft.Compute/availabilitySets/" + availabilityset.name, availabilityset.location);
                _resources.Add(availabilityset);
                _logProvider.WriteLog("BuildAvailabilitySetObject", "Microsoft.Compute/availabilitySets/" + availabilityset.name);
            }
            catch { }

            _logProvider.WriteLog("BuildAvailabilitySetObject", "End");
        }

        private void BuildLoadBalancerObject(string subscriptionId, XmlNode cloudservice, string loadbalancername, AsmArtefacts artefacts, string token)
        {
            _logProvider.WriteLog("BuildLoadBalancerObject", "Start");

            LoadBalancer loadbalancer = new LoadBalancer();
            loadbalancer.name = loadbalancername;
            loadbalancer.location = cloudservice.SelectSingleNode("//HostedServiceProperties/ExtendedProperties/ExtendedProperty[Name='ResourceLocation']/Value").InnerText;

            FrontendIPConfiguration_Properties frontendipconfiguration_properties = new FrontendIPConfiguration_Properties();

            // if internal load balancer
            if (cloudservice.SelectNodes("//Deployments/Deployment/LoadBalancers/LoadBalancer/FrontendIpConfiguration/Type").Count > 0)
            {
                string virtualnetworkname = cloudservice.SelectSingleNode("//Deployments/Deployment/VirtualNetworkName").InnerText;
                string subnetname = cloudservice.SelectSingleNode("//Deployments/Deployment/LoadBalancers/LoadBalancer/FrontendIpConfiguration/SubnetName").InnerText.Replace(" ", "");

                frontendipconfiguration_properties.privateIPAllocationMethod = "Dynamic";
                if (cloudservice.SelectNodes("//Deployments/Deployment/LoadBalancers/LoadBalancer/FrontendIpConfiguration/StaticVirtualNetworkIPAddress").Count > 0)
                {
                    frontendipconfiguration_properties.privateIPAllocationMethod = "Static";
                    frontendipconfiguration_properties.privateIPAddress = cloudservice.SelectSingleNode("//Deployments/Deployment/LoadBalancers/LoadBalancer/FrontendIpConfiguration/StaticVirtualNetworkIPAddress").InnerText;
                }

                List<string> dependson = new List<string>();
                if (GetProcessedItem("Microsoft.Network/virtualNetworks/" + virtualnetworkname))
                {
                    dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "')]");
                }
                loadbalancer.dependsOn = dependson;

                Reference subnet_ref = new Reference();
                subnet_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "/subnets/" + subnetname + "')]";
                frontendipconfiguration_properties.subnet = subnet_ref;
            }
            // if external load balancer
            else
            {
                List<string> dependson = new List<string>();
                dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/publicIPAddresses/" + loadbalancer.name + "-PIP')]");
                loadbalancer.dependsOn = dependson;

                Reference publicipaddress_ref = new Reference();
                publicipaddress_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/publicIPAddresses/" + loadbalancer.name + "-PIP')]";
                frontendipconfiguration_properties.publicIPAddress = publicipaddress_ref;
            }


            LoadBalancer_Properties loadbalancer_properties = new LoadBalancer_Properties();

            FrontendIPConfiguration frontendipconfiguration = new FrontendIPConfiguration();
            frontendipconfiguration.properties = frontendipconfiguration_properties;

            List<FrontendIPConfiguration> frontendipconfigurations = new List<FrontendIPConfiguration>();
            frontendipconfigurations.Add(frontendipconfiguration);
            loadbalancer_properties.frontendIPConfigurations = frontendipconfigurations;

            Hashtable backendaddresspool = new Hashtable();
            backendaddresspool.Add("name", "default");
            List<Hashtable> backendaddresspools = new List<Hashtable>();
            backendaddresspools.Add(backendaddresspool);
            loadbalancer_properties.backendAddressPools = backendaddresspools;

            List<InboundNatRule> inboundnatrules = new List<InboundNatRule>();
            List<LoadBalancingRule> loadbalancingrules = new List<LoadBalancingRule>();
            List<Probe> probes = new List<Probe>();

            foreach (var cloudServiceVM in artefacts.VirtualMachines)
            {
                string deploymentName;
                string virtualNetworkName;
                string loadBalancerName;
                _asmRetriever.GetVMDetails(subscriptionId, cloudServiceVM.CloudService, cloudServiceVM.VirtualMachine, token, out deploymentName, out virtualNetworkName, out loadBalancerName);


                if (loadBalancerName == loadbalancer.name)
                {
                    //process VM
 
                    Hashtable virtualmachineinfo = new Hashtable();
                    virtualmachineinfo.Add("cloudservicename", cloudServiceVM.CloudService);
                    virtualmachineinfo.Add("deploymentname", deploymentName);
                    virtualmachineinfo.Add("virtualmachinename", cloudServiceVM.VirtualMachine);
                    XmlDocument virtualmachine = _asmRetriever.GetAzureASMResources("VirtualMachine", subscriptionId, virtualmachineinfo, token);

                    BuildLoadBalancerRules(virtualmachine, loadbalancer.name, ref inboundnatrules, ref loadbalancingrules, ref probes);
                }
            }

            loadbalancer_properties.inboundNatRules = inboundnatrules;
            loadbalancer_properties.loadBalancingRules = loadbalancingrules;
            loadbalancer_properties.probes = probes;
            loadbalancer.properties = loadbalancer_properties;

            try // it fails if this load balancer was already processed. safe to continue.
            {
                _processedItems.Add("Microsoft.Network/loadBalancers/" + loadbalancer.name, loadbalancer.location);
                _resources.Add(loadbalancer);
                _logProvider.WriteLog("BuildLoadBalancerObject", "Microsoft.Network/loadBalancers/" + loadbalancer.name);
            }
            catch { }

            _logProvider.WriteLog("BuildLoadBalancerObject", "End");
        }

        private void BuildLoadBalancerRules(XmlNode resource, string loadbalancername, ref List<InboundNatRule> inboundnatrules, ref List<LoadBalancingRule> loadbalancingrules, ref List<Probe> probes)
        {
            _logProvider.WriteLog("BuildLoadBalancerRules", "Start");

            string virtualmachinename = resource.SelectSingleNode("//RoleName").InnerText;

            foreach (XmlNode inputendpoint in resource.SelectNodes("//ConfigurationSets/ConfigurationSet/InputEndpoints/InputEndpoint"))
            {
                if (inputendpoint.SelectSingleNode("LoadBalancedEndpointSetName") == null) // if it's a inbound nat rule
                {
                    InboundNatRule_Properties inboundnatrule_properties = new InboundNatRule_Properties();
                    inboundnatrule_properties.frontendPort = Int64.Parse(inputendpoint.SelectSingleNode("Port").InnerText);
                    inboundnatrule_properties.backendPort = Int64.Parse(inputendpoint.SelectSingleNode("LocalPort").InnerText);
                    inboundnatrule_properties.protocol = inputendpoint.SelectSingleNode("Protocol").InnerText;

                    Reference frontendIPConfiguration = new Reference();
                    frontendIPConfiguration.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/frontendIPConfigurations/default')]";
                    inboundnatrule_properties.frontendIPConfiguration = frontendIPConfiguration;

                    InboundNatRule inboundnatrule = new InboundNatRule();
                    inboundnatrule.name = virtualmachinename + "-" + inputendpoint.SelectSingleNode("Name").InnerText;
                    inboundnatrule.name = inboundnatrule.name.Replace(" ", "");
                    inboundnatrule.properties = inboundnatrule_properties;

                    inboundnatrules.Add(inboundnatrule);
                }
                else // if it's a load balancing rule
                {
                    string name = inputendpoint.SelectSingleNode("LoadBalancedEndpointSetName").InnerText;
                    XmlNode probenode = inputendpoint.SelectSingleNode("LoadBalancerProbe");

                    // build probe
                    Probe_Properties probe_properties = new Probe_Properties();
                    probe_properties.port = Int64.Parse(probenode.SelectSingleNode("Port").InnerText);
                    probe_properties.protocol = probenode.SelectSingleNode("Protocol").InnerText;

                    Probe probe = new Probe();
                    probe.name = name;
                    probe.properties = probe_properties;

                    try // fails if this probe already exists. safe to continue
                    {
                        _processedItems.Add("Microsoft.Network/loadBalancers/" + loadbalancername + "/probes/" + probe.name, "");
                        probes.Add(probe);
                    }
                    catch { }

                    // build load balancing rule
                    Reference frontendipconfiguration_ref = new Reference();
                    frontendipconfiguration_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/frontendIPConfigurations/default')]";

                    Reference backendaddresspool_ref = new Reference();
                    backendaddresspool_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/backendAddressPools/default')]";

                    Reference probe_ref = new Reference();
                    probe_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/probes/" + probe.name + "')]";

                    LoadBalancingRule_Properties loadbalancingrule_properties = new LoadBalancingRule_Properties();
                    loadbalancingrule_properties.frontendIPConfiguration = frontendipconfiguration_ref;
                    loadbalancingrule_properties.backendAddressPool = backendaddresspool_ref;
                    loadbalancingrule_properties.probe = probe_ref;
                    loadbalancingrule_properties.frontendPort = Int64.Parse(inputendpoint.SelectSingleNode("Port").InnerText);
                    loadbalancingrule_properties.backendPort = Int64.Parse(inputendpoint.SelectSingleNode("LocalPort").InnerText);
                    loadbalancingrule_properties.protocol = inputendpoint.SelectSingleNode("Protocol").InnerText;

                    LoadBalancingRule loadbalancingrule = new LoadBalancingRule();
                    loadbalancingrule.name = name;
                    loadbalancingrule.properties = loadbalancingrule_properties;

                    try // fails if this load balancing rule already exists. safe to continue
                    {
                        _processedItems.Add("Microsoft.Network/loadBalancers/" + loadbalancername + "/loadBalancingRules/" + loadbalancingrule.name, "");
                        loadbalancingrules.Add(loadbalancingrule);
                        _logProvider.WriteLog("BuildLoadBalancerRules", "Microsoft.Network/loadBalancers/" + loadbalancername + "/loadBalancingRules/" + loadbalancingrule.name);
                    }
                    catch { continue; }
                }
            }

            _logProvider.WriteLog("BuildLoadBalancerRules", "End");
        }


        private void BuildNewVirtualNetworkObject(XmlNode resource, Hashtable info)
        {
            //string ipaddress = resource.SelectSingleNode("Deployments/Deployment/RoleInstanceList/RoleInstance/IpAddress").InnerText;
            //IPAddress ipaddressIP = IPAddress.Parse(ipaddress);
            //byte[] ipaddressBYTE = ipaddressIP.GetAddressBytes();

            //IPAddress subnetmaskIP = IPAddress.Parse("255.255.254.0");
            //byte[] subnetmaskBYTE = subnetmaskIP.GetAddressBytes();

            //byte[] addressprefixBYTE = new byte[4];
            //addressprefixBYTE[0] = (byte)((byte)ipaddressBYTE[0] & (byte)subnetmaskBYTE[0]);
            //addressprefixBYTE[1] = (byte)((byte)ipaddressBYTE[1] & (byte)subnetmaskBYTE[1]);
            //addressprefixBYTE[2] = (byte)((byte)ipaddressBYTE[2] & (byte)subnetmaskBYTE[2]);
            //addressprefixBYTE[3] = (byte)((byte)ipaddressBYTE[3] & (byte)subnetmaskBYTE[3]);

            //string addressprefix = "";
            //addressprefix += addressprefixBYTE[0].ToString() + ".";
            //addressprefix += addressprefixBYTE[1].ToString() + ".";
            //addressprefix += addressprefixBYTE[2].ToString() + ".";
            //addressprefix += addressprefixBYTE[3].ToString();

            _logProvider.WriteLog("BuildNewVirtualNetworkObject", "Start");

            List<string> addressprefixes = new List<string>();
            addressprefixes.Add("192.168.0.0/23");

            AddressSpace addressspace = new AddressSpace();
            addressspace.addressPrefixes = addressprefixes;

            VirtualNetwork virtualnetwork = new VirtualNetwork();
            virtualnetwork.name = info["cloudservicename"].ToString() + "-VNET";
            virtualnetwork.location = resource.SelectSingleNode("//HostedServiceProperties/ExtendedProperties/ExtendedProperty[Name='ResourceLocation']/Value").InnerText;

            List<Subnet> subnets = new List<Subnet>();
            Subnet_Properties properties = new Subnet_Properties();
            properties.addressPrefix = "192.168.0.0/23";

            Subnet subnet = new Subnet();
            subnet.name = "Subnet1";
            subnet.properties = properties;

            subnets.Add(subnet);

            VirtualNetwork_Properties virtualnetwork_properties = new VirtualNetwork_Properties();
            virtualnetwork_properties.addressSpace = addressspace;
            virtualnetwork_properties.subnets = subnets;

            virtualnetwork.properties = virtualnetwork_properties;

            try
            {
                _processedItems.Add("Microsoft.Network/virtualNetworks/" + virtualnetwork.name, virtualnetwork.location);
                _resources.Add(virtualnetwork);
                _logProvider.WriteLog("BuildNewVirtualNetworkObject", "Microsoft.Network/virtualNetworks/" + virtualnetwork.name);
            }
            catch { }

            _logProvider.WriteLog("BuildNewVirtualNetworkObject", "End");
        }

        private void BuildVirtualNetworkObject(string subscriptionId, XmlNode resource, string token)
        {
            _logProvider.WriteLog("BuildVirtualNetworkObject", "Start");

            List<string> dependson = new List<string>();

            List<string> addressprefixes = new List<string>();
            foreach (XmlNode addressprefix in resource.SelectNodes("AddressSpace/AddressPrefixes"))
            {
                addressprefixes.Add(addressprefix.SelectSingleNode("AddressPrefix").InnerText);
            }

            AddressSpace addressspace = new AddressSpace();
            addressspace.addressPrefixes = addressprefixes;

            List<string> dnsservers = new List<string>();
            foreach (XmlNode dnsserver in resource.SelectNodes("Dns/DnsServers/DnsServer"))
            {
                dnsservers.Add(dnsserver.SelectSingleNode("Address").InnerText);
            }

            VirtualNetwork_dhcpOptions dhcpoptions = new VirtualNetwork_dhcpOptions();
            dhcpoptions.dnsServers = dnsservers;

            VirtualNetwork virtualnetwork = new VirtualNetwork();
            virtualnetwork.name = resource.SelectSingleNode("Name").InnerText.Replace(" ", "");
            // get location if virtual network was not deployed in a affinity group
            if (resource.SelectNodes("Location").Count > 0)
            {
                virtualnetwork.location = resource.SelectSingleNode("Location").InnerText;
            }
            // get location if virtual network was deployed in a affinity group
            else
            {
                Hashtable affinitygroupinfo = new Hashtable();
                affinitygroupinfo.Add("affinitygroupname", resource.SelectSingleNode("AffinityGroup").InnerText);
                XmlDocument affinitygroup = _asmRetriever.GetAzureASMResources("AffinityGroup", subscriptionId, affinitygroupinfo, token);

                virtualnetwork.location = affinitygroup.SelectSingleNode("//Location").InnerText;
            }
            virtualnetwork.dependsOn = dependson;

            List<Subnet> subnets = new List<Subnet>();
            foreach (XmlNode subnetnode in resource.SelectNodes("Subnets/Subnet"))
            {
                Subnet_Properties properties = new Subnet_Properties();
                properties.addressPrefix = subnetnode.SelectSingleNode("AddressPrefix").InnerText;

                Subnet subnet = new Subnet();
                subnet.name = subnetnode.SelectSingleNode("Name").InnerText.Replace(" ", "");
                subnet.properties = properties;

                subnets.Add(subnet);

                // add Network Security Group if exists
                if (subnetnode.SelectNodes("NetworkSecurityGroup").Count > 0)
                {
                    NetworkSecurityGroup networksecuritygroup = BuildNetworkSecurityGroup(subscriptionId, subnetnode.SelectSingleNode("NetworkSecurityGroup").InnerText, token);

                    // Add NSG reference to the subnet
                    Reference networksecuritygroup_ref = new Reference();
                    networksecuritygroup_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/networkSecurityGroups/" + networksecuritygroup.name + "')]";

                    properties.networkSecurityGroup = networksecuritygroup_ref;

                    // Add NSG dependsOn to the Virtual Network object
                    if (!virtualnetwork.dependsOn.Contains(networksecuritygroup_ref.id))
                    {
                        virtualnetwork.dependsOn.Add(networksecuritygroup_ref.id);
                    }
                }

                // add Route Table if exists
                if (subnetnode.SelectNodes("RouteTableName").Count > 0)
                {
                    RouteTable routetable = BuildRouteTable(subscriptionId, subnetnode.SelectSingleNode("RouteTableName").InnerText, token);

                    // Add Route Table reference to the subnet
                    Reference routetable_ref = new Reference();
                    routetable_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/routeTables/" + routetable.name + "')]";

                    properties.routeTable = routetable_ref;

                    // Add Route Table dependsOn to the Virtual Network object
                    if (!virtualnetwork.dependsOn.Contains(routetable_ref.id))
                    {
                        virtualnetwork.dependsOn.Add(routetable_ref.id);
                    }
                }
            }

            VirtualNetwork_Properties virtualnetwork_properties = new VirtualNetwork_Properties();
            virtualnetwork_properties.addressSpace = addressspace;
            virtualnetwork_properties.subnets = subnets;
            virtualnetwork_properties.dhcpOptions = dhcpoptions;

            virtualnetwork.properties = virtualnetwork_properties;

            _processedItems.Add("Microsoft.Network/virtualNetworks/" + virtualnetwork.name, virtualnetwork.location);
            _resources.Add(virtualnetwork);
            _logProvider.WriteLog("BuildVirtualNetworkObject", "Microsoft.Network/virtualNetworks/" + virtualnetwork.name);


            // Process Virtual Network Gateway if one exists
            if (resource.SelectNodes("Gateway").Count > 0)
            {
                // Gateway Public IP Address
                PublicIPAddress_Properties publicipaddress_properties = new PublicIPAddress_Properties();
                publicipaddress_properties.publicIPAllocationMethod = "Dynamic";

                PublicIPAddress publicipaddress = new PublicIPAddress();
                publicipaddress.name = virtualnetwork.name + "-VPNGateway-PIP";
                publicipaddress.location = virtualnetwork.location;
                publicipaddress.properties = publicipaddress_properties;

                _processedItems.Add("Microsoft.Network/publicIPAddresses/" + publicipaddress.name, publicipaddress.location);
                _resources.Add(publicipaddress);

                // Virtual Network Gateway
                Reference subnet_ref = new Reference();
                subnet_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetwork.name + "/subnets/GatewaySubnet')]";

                Reference publicipaddress_ref = new Reference();
                publicipaddress_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/publicIPAddresses/" + publicipaddress.name + "')]";

                dependson = new List<string>();
                dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetwork.name + "')]");
                dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/publicIPAddresses/" + publicipaddress.name + "')]");

                IpConfiguration_Properties ipconfiguration_properties = new IpConfiguration_Properties();
                ipconfiguration_properties.privateIPAllocationMethod = "Dynamic";
                ipconfiguration_properties.subnet = subnet_ref;
                ipconfiguration_properties.publicIPAddress = publicipaddress_ref;

                IpConfiguration virtualnetworkgateway_ipconfiguration = new IpConfiguration();
                virtualnetworkgateway_ipconfiguration.name = "VPNGatewayIPConfig";
                virtualnetworkgateway_ipconfiguration.properties = ipconfiguration_properties;

                VirtualNetworkGateway_Sku virtualnetworkgateway_sku = new VirtualNetworkGateway_Sku();
                virtualnetworkgateway_sku.name = "Basic";
                virtualnetworkgateway_sku.tier = "Basic";

                List<IpConfiguration> virtualnetworkgateway_ipconfigurations = new List<IpConfiguration>();
                virtualnetworkgateway_ipconfigurations.Add(virtualnetworkgateway_ipconfiguration);

                VirtualNetworkGateway_Properties virtualnetworkgateway_properties = new VirtualNetworkGateway_Properties();
                virtualnetworkgateway_properties.ipConfigurations = virtualnetworkgateway_ipconfigurations;
                virtualnetworkgateway_properties.sku = virtualnetworkgateway_sku;
                virtualnetworkgateway_properties.gatewayType = "Vpn";

                // If there is VPN Client configuration
                if (resource.SelectNodes("Gateway/VPNClientAddressPool/AddressPrefixes/AddressPrefix").Count > 0)
                {
                    addressprefixes = new List<string>();
                    addressprefixes.Add(resource.SelectNodes("Gateway/VPNClientAddressPool/AddressPrefixes/AddressPrefix")[0].InnerText);

                    AddressSpace vpnclientaddresspool = new AddressSpace();
                    vpnclientaddresspool.addressPrefixes = addressprefixes;

                    VPNClientConfiguration vpnclientconfiguration = new VPNClientConfiguration();
                    vpnclientconfiguration.vpnClientAddressPool = vpnclientaddresspool;

                    //Process vpnClientRootCertificates
                    Hashtable infocrc = new Hashtable();
                    infocrc.Add("virtualnetworkname", resource.SelectSingleNode("Name").InnerText);
                    XmlDocument clientrootcertificates = _asmRetriever.GetAzureASMResources("ClientRootCertificates", subscriptionId, infocrc, token);

                    List<VPNClientCertificate> vpnclientrootcertificates = new List<VPNClientCertificate>();
                    foreach (XmlNode certificate in clientrootcertificates.SelectNodes("//ClientRootCertificate"))
                    {
                        Hashtable infocert = new Hashtable();
                        infocert.Add("virtualnetworkname", resource.SelectSingleNode("Name").InnerText);
                        infocert.Add("thumbprint", certificate.SelectSingleNode("Thumbprint").InnerText);
                        XmlDocument clientrootcertificate = _asmRetriever.GetAzureASMResources("ClientRootCertificate", subscriptionId, infocert, token);

                        VPNClientCertificate_Properties vpnclientcertificate_properties = new VPNClientCertificate_Properties();
                        vpnclientcertificate_properties.PublicCertData = Convert.ToBase64String(StrToByteArray(clientrootcertificate.InnerText));

                        VPNClientCertificate vpnclientcertificate = new VPNClientCertificate();
                        vpnclientcertificate.name = certificate.SelectSingleNode("Subject").InnerText.Replace("CN=", "");
                        vpnclientcertificate.properties = vpnclientcertificate_properties;

                        vpnclientrootcertificates.Add(vpnclientcertificate);
                    }

                    vpnclientconfiguration.vpnClientRootCertificates = vpnclientrootcertificates;

                    virtualnetworkgateway_properties.vpnClientConfiguration = vpnclientconfiguration;
                }

                Hashtable virtualnetworkgatewayinfo = new Hashtable();
                virtualnetworkgatewayinfo.Add("virtualnetworkname", resource.SelectSingleNode("Name").InnerText);
                virtualnetworkgatewayinfo.Add("localnetworksitename", "");
                XmlDocument gateway = _asmRetriever.GetAzureASMResources("VirtualNetworkGateway", subscriptionId, virtualnetworkgatewayinfo, token);

                string vpnType = gateway.SelectSingleNode("//GatewayType").InnerText;
                if (vpnType == "StaticRouting")
                {
                    vpnType = "PolicyBased";
                }
                else if (vpnType == "DynamicRouting")
                {
                    vpnType = "RouteBased";
                }
                virtualnetworkgateway_properties.vpnType = vpnType;

                VirtualNetworkGateway virtualnetworkgateway = new VirtualNetworkGateway();
                virtualnetworkgateway.location = virtualnetwork.location;
                virtualnetworkgateway.name = virtualnetwork.name + "-VPNGateway";
                virtualnetworkgateway.properties = virtualnetworkgateway_properties;
                virtualnetworkgateway.dependsOn = dependson;

                _processedItems.Add("Microsoft.Network/virtualNetworkGateways/" + virtualnetworkgateway.name, virtualnetworkgateway.location);
                _resources.Add(virtualnetworkgateway);
                _logProvider.WriteLog("BuildVirtualNetworkObject", "Microsoft.Network/virtualNetworkGateways/" + virtualnetworkgateway.name);

                // Local Network Gateways & Connections
                foreach (XmlNode LocalNetworkSite in resource.SelectNodes("Gateway/Sites/LocalNetworkSite"))
                {
                    // Local Network Gateway
                    addressprefixes = new List<string>();
                    foreach (XmlNode addressprefix in LocalNetworkSite.SelectNodes("AddressSpace/AddressPrefixes"))
                    {
                        addressprefixes.Add(addressprefix.SelectSingleNode("AddressPrefix").InnerText);
                    }

                    AddressSpace localnetworkaddressspace = new AddressSpace();
                    localnetworkaddressspace.addressPrefixes = addressprefixes;

                    LocalNetworkGateway_Properties localnetworkgateway_properties = new LocalNetworkGateway_Properties();
                    localnetworkgateway_properties.localNetworkAddressSpace = localnetworkaddressspace;
                    localnetworkgateway_properties.gatewayIpAddress = LocalNetworkSite.SelectSingleNode("VpnGatewayAddress").InnerText;

                    LocalNetworkGateway localnetworkgateway = new LocalNetworkGateway();
                    localnetworkgateway.name = LocalNetworkSite.SelectSingleNode("Name").InnerText + "-LocalGateway";
                    localnetworkgateway.name = localnetworkgateway.name.Replace(" ", "");

                    localnetworkgateway.location = virtualnetwork.location;
                    localnetworkgateway.properties = localnetworkgateway_properties;

                    try
                    {
                        _processedItems.Add("Microsoft.Network/localNetworkGateways/" + localnetworkgateway.name, localnetworkgateway.location);
                        _resources.Add(localnetworkgateway);
                    }
                    catch { }
                    _logProvider.WriteLog("BuildVirtualNetworkObject", "Microsoft.Network/localNetworkGateways/" + localnetworkgateway.name);

                    // Connections
                    Reference virtualnetworkgateway_ref = new Reference();
                    virtualnetworkgateway_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworkGateways/" + virtualnetworkgateway.name + "')]";

                    Reference localnetworkgateway_ref = new Reference();
                    localnetworkgateway_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/localNetworkGateways/" + localnetworkgateway.name + "')]";

                    dependson = new List<string>();
                    dependson.Add(virtualnetworkgateway_ref.id);
                    dependson.Add(localnetworkgateway_ref.id);

                    GatewayConnection_Properties gatewayconnection_properties = new GatewayConnection_Properties();
                    gatewayconnection_properties.connectionType = LocalNetworkSite.SelectSingleNode("Connections/Connection/Type").InnerText;
                    gatewayconnection_properties.virtualNetworkGateway1 = virtualnetworkgateway_ref;
                    gatewayconnection_properties.localNetworkGateway2 = localnetworkgateway_ref;

                    virtualnetworkgatewayinfo["localnetworksitename"] = LocalNetworkSite.SelectSingleNode("Name").InnerText;
                    XmlDocument connectionsharekey = _asmRetriever.GetAzureASMResources("VirtualNetworkGatewaySharedKey", subscriptionId, virtualnetworkgatewayinfo, token);

                    if (!(connectionsharekey == null))
                    {
                        gatewayconnection_properties.sharedKey = connectionsharekey.SelectSingleNode("//Value").InnerText;

                        GatewayConnection gatewayconnection = new GatewayConnection();
                        gatewayconnection.name = virtualnetworkgateway.name + "-" + localnetworkgateway.name + "-connection";
                        gatewayconnection.location = virtualnetwork.location;
                        gatewayconnection.properties = gatewayconnection_properties;
                        gatewayconnection.dependsOn = dependson;

                        _processedItems.Add("Microsoft.Network/connections/" + gatewayconnection.name, gatewayconnection.location);
                        _resources.Add(gatewayconnection);
                        _logProvider.WriteLog("BuildVirtualNetworkObject", "Microsoft.Network/connections/" + gatewayconnection.name);
                    }
                    else
                    {
                        _logProvider.WriteLog("BuildVirtualNetworkObject", "Microsoft.Network/connections/" + " NO SHARED KEY DEFINED");
                    }
                }
            }

            _logProvider.WriteLog("BuildVirtualNetworkObject", "End");
        }



        private NetworkSecurityGroup BuildNetworkSecurityGroup(string subscriptionId, string networksecuritygroupname, string token)
        {
            _logProvider.WriteLog("BuildNetworkSecurityGroup", "Start");

            Hashtable nsginfo = new Hashtable();
            nsginfo.Add("name", networksecuritygroupname);
            XmlDocument resource = _asmRetriever.GetAzureASMResources("NetworkSecurityGroup", subscriptionId, nsginfo, token);

            NetworkSecurityGroup networksecuritygroup = new NetworkSecurityGroup();
            networksecuritygroup.name = resource.SelectSingleNode("//Name").InnerText.Replace(' ', '-');
            networksecuritygroup.location = resource.SelectSingleNode("//Location").InnerText;

            NetworkSecurityGroup_Properties networksecuritygroup_properties = new NetworkSecurityGroup_Properties();
            networksecuritygroup_properties.securityRules = new List<SecurityRule>();

            // for each rule
            foreach (XmlNode rule in resource.SelectNodes("//Rules/Rule"))
            {
                // if not system rule
                if (rule.SelectNodes("IsDefault").Count == 0)
                {
                    SecurityRule_Properties securityrule_properties = new SecurityRule_Properties();
                    securityrule_properties.description = rule.SelectSingleNode("Name").InnerText;
                    securityrule_properties.direction = rule.SelectSingleNode("Type").InnerText;
                    securityrule_properties.priority = long.Parse(rule.SelectSingleNode("Priority").InnerText);
                    securityrule_properties.access = rule.SelectSingleNode("Action").InnerText;
                    securityrule_properties.sourceAddressPrefix = rule.SelectSingleNode("SourceAddressPrefix").InnerText;
                    securityrule_properties.sourceAddressPrefix.Replace("_", "");
                    securityrule_properties.destinationAddressPrefix = rule.SelectSingleNode("DestinationAddressPrefix").InnerText;
                    securityrule_properties.destinationAddressPrefix.Replace("_", "");
                    securityrule_properties.sourcePortRange = rule.SelectSingleNode("SourcePortRange").InnerText;
                    securityrule_properties.destinationPortRange = rule.SelectSingleNode("DestinationPortRange").InnerText;
                    securityrule_properties.protocol = rule.SelectSingleNode("Protocol").InnerText;

                    SecurityRule securityrule = new SecurityRule();
                    securityrule.name = rule.SelectSingleNode("Name").InnerText.Replace(' ', '-');
                    securityrule.properties = securityrule_properties;

                    networksecuritygroup_properties.securityRules.Add(securityrule);
                }
            }

            networksecuritygroup.properties = networksecuritygroup_properties;

            try // it fails if this network security group was already processed. safe to continue.
            {
                _processedItems.Add("Microsoft.Network/networkSecurityGroups/" + networksecuritygroup.name, networksecuritygroup.location);
                _resources.Add(networksecuritygroup);
                _logProvider.WriteLog("BuildNetworkSecurityGroup", "Microsoft.Network/networkSecurityGroups/" + networksecuritygroup.name);
            }
            catch { }

            _logProvider.WriteLog("BuildNetworkSecurityGroup", "End");

            return networksecuritygroup;
        }

        private RouteTable BuildRouteTable(string subscriptionId, string routetablename, string token)
        {
            _logProvider.WriteLog("BuildRouteTable", "Start");

            Hashtable info = new Hashtable();
            info.Add("name", routetablename);
            XmlDocument resource = _asmRetriever.GetAzureASMResources("RouteTable", subscriptionId, info, token);

            RouteTable routetable = new RouteTable();
            routetable.name = resource.SelectSingleNode("//Name").InnerText;
            routetable.location = resource.SelectSingleNode("//Location").InnerText;

            RouteTable_Properties routetable_properties = new RouteTable_Properties();
            routetable_properties.routes = new List<Route>();

            // for each route
            foreach (XmlNode routenode in resource.SelectNodes("//RouteList/Route"))
            {
                //securityrule_properties.protocol = rule.SelectSingleNode("Protocol").InnerText;
                Route_Properties route_properties = new Route_Properties();
                route_properties.addressPrefix = routenode.SelectSingleNode("AddressPrefix").InnerText;

                // convert next hop type string
                switch (routenode.SelectSingleNode("NextHopType/Type").InnerText)
                {
                    case "VirtualAppliance":
                        route_properties.nextHopType = "VirtualAppliance";
                        break;
                    case "VPNGateway":
                        route_properties.nextHopType = "VirtualNetworkGateway";
                        break;
                    case "Internet":
                        route_properties.nextHopType = "Internet";
                        break;
                    case "VNETLocal":
                        route_properties.nextHopType = "VnetLocal";
                        break;
                    case "Null":
                        route_properties.nextHopType = "None";
                        break;
                }
                if (route_properties.nextHopType == "VirtualAppliance")
                    route_properties.nextHopIpAddress = routenode.SelectSingleNode("NextHopType/IpAddress").InnerText;

                Route route = new Route();
                route.name = routenode.SelectSingleNode("Name").InnerText.Replace(' ', '-');
                route.properties = route_properties;

                routetable_properties.routes.Add(route);
            }

            routetable.properties = routetable_properties;

            if (!_resources.Contains(routetable))
            {
                _processedItems.Add("Microsoft.Network/routeTables/" + routetable.name, routetable.location);
                _resources.Add(routetable);
                _logProvider.WriteLog("BuildRouteTable", "Microsoft.Network/routeTables/" + routetable.name);
            }

            _logProvider.WriteLog("BuildRouteTable", "End");

            return routetable;
        }

        private void BuildNetworkInterfaceObject(string subscriptionId, XmlNode resource, Hashtable virtualmachineinfo, ref List<NetworkProfile_NetworkInterface> networkinterfaces, string token)
        {
            _logProvider.WriteLog("BuildNetworkInterfaceObject", "Start");

            string virtualmachinename = virtualmachineinfo["virtualmachinename"].ToString();
            string cloudservicename = virtualmachineinfo["cloudservicename"].ToString();
            string deploymentname = virtualmachineinfo["deploymentname"].ToString();
            string virtualnetworkname = virtualmachineinfo["virtualnetworkname"].ToString();
            string loadbalancername = virtualmachineinfo["loadbalancername"].ToString();
            string subnet_name = "";

            if (virtualnetworkname != "empty")
            {
                virtualnetworkname = virtualmachineinfo["virtualnetworkname"].ToString().Replace(" ", "");
                subnet_name = resource.SelectSingleNode("//ConfigurationSets/ConfigurationSet/SubnetNames[1]/SubnetName").InnerText.Replace(" ", "");
            }
            else
            {
                virtualnetworkname = cloudservicename + "-VNET";
                subnet_name = "Subnet1";
            }

            Reference subnet_ref = new Reference();
            subnet_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "/subnets/" + subnet_name + "')]";

            string privateIPAllocationMethod = "Dynamic";
            string privateIPAddress = null;
            if (resource.SelectSingleNode("//ConfigurationSets/ConfigurationSet[1]/StaticVirtualNetworkIPAddress") != null)
            {
                privateIPAllocationMethod = "Static";
                privateIPAddress = resource.SelectSingleNode("//ConfigurationSets/ConfigurationSet[1]/StaticVirtualNetworkIPAddress").InnerText;
            }
            // if its a VM not connected to a virtual network
            //else if (virtualmachineinfo["virtualnetworkname"].ToString() == "empty")
            //{
            //    privateIPAllocationMethod = "Static";
            //    privateIPAddress = virtualmachineinfo["ipaddress"].ToString();
            //}

            // Get the list of endpoints
            XmlNodeList inputendpoints = resource.SelectNodes("//ConfigurationSets/ConfigurationSet/InputEndpoints/InputEndpoint");

            // If there is at least one endpoint add the reference to the LB backend pool
            List<Reference> loadBalancerBackendAddressPools = new List<Reference>();
            if (inputendpoints.Count > 0)
            {
                Reference loadBalancerBackendAddressPool = new Reference();
                loadBalancerBackendAddressPool.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/backendAddressPools/default')]";

                loadBalancerBackendAddressPools.Add(loadBalancerBackendAddressPool);
            }

            // Adds the references to the inboud nat rules
            List<Reference> loadBalancerInboundNatRules = new List<Reference>();
            foreach (XmlNode inputendpoint in inputendpoints)
            {
                if (inputendpoint.SelectSingleNode("LoadBalancedEndpointSetName") == null) // don't want to add a load balance endpoint as an inbound nat rule
                {
                    string inboundnatrulename = virtualmachinename + "-" + inputendpoint.SelectSingleNode("Name").InnerText;
                    inboundnatrulename = inboundnatrulename.Replace(" ", "");

                    Reference loadBalancerInboundNatRule = new Reference();
                    loadBalancerInboundNatRule.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "/inboundNatRules/" + inboundnatrulename + "')]";

                    loadBalancerInboundNatRules.Add(loadBalancerInboundNatRule);
                }
            }

            IpConfiguration_Properties ipconfiguration_properties = new IpConfiguration_Properties();
            ipconfiguration_properties.privateIPAllocationMethod = privateIPAllocationMethod;
            ipconfiguration_properties.privateIPAddress = privateIPAddress;
            ipconfiguration_properties.subnet = subnet_ref;
            ipconfiguration_properties.loadBalancerInboundNatRules = loadBalancerInboundNatRules;

            // basic size VMs cannot have load balancer rules
            if (!resource.SelectSingleNode("//RoleSize").InnerText.Contains("Basic"))
            {
                ipconfiguration_properties.loadBalancerBackendAddressPools = loadBalancerBackendAddressPools;
            }

            string ipconfiguration_name = "ipconfig1";
            IpConfiguration ipconfiguration = new IpConfiguration();
            ipconfiguration.name = ipconfiguration_name;
            ipconfiguration.properties = ipconfiguration_properties;

            List<IpConfiguration> ipConfigurations = new List<IpConfiguration>();
            ipConfigurations.Add(ipconfiguration);

            NetworkInterface_Properties networkinterface_properties = new NetworkInterface_Properties();
            networkinterface_properties.ipConfigurations = ipConfigurations;
            if (resource.SelectNodes("//ConfigurationSets/ConfigurationSet/IPForwarding").Count > 0)
            {
                networkinterface_properties.enableIPForwarding = true;
            }

            List<string> dependson = new List<string>();
            if (GetProcessedItem("Microsoft.Network/virtualNetworks/" + virtualnetworkname))
            {
                dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "')]");
            }
            dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/loadBalancers/" + loadbalancername + "')]");

            string networkinterface_name = virtualmachinename;
            NetworkInterface networkinterface = new NetworkInterface();
            networkinterface.name = networkinterface_name;
            networkinterface.location = virtualmachineinfo["location"].ToString();
            networkinterface.properties = networkinterface_properties;
            networkinterface.dependsOn = dependson;

            NetworkProfile_NetworkInterface_Properties networkinterface_ref_properties = new NetworkProfile_NetworkInterface_Properties();
            networkinterface_ref_properties.primary = true;

            NetworkProfile_NetworkInterface networkinterface_ref = new NetworkProfile_NetworkInterface();
            networkinterface_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/networkInterfaces/" + networkinterface.name + "')]";
            networkinterface_ref.properties = networkinterface_ref_properties;

            if (resource.SelectNodes("//ConfigurationSets/ConfigurationSet/NetworkSecurityGroup").Count > 0)
            {
                NetworkSecurityGroup networksecuritygroup = BuildNetworkSecurityGroup(subscriptionId, resource.SelectSingleNode("//ConfigurationSets/ConfigurationSet/NetworkSecurityGroup").InnerText, token);

                // Add NSG reference to the network interface
                Reference networksecuritygroup_ref = new Reference();
                networksecuritygroup_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/networkSecurityGroups/" + networksecuritygroup.name + "')]";

                networkinterface_properties.NetworkSecurityGroup = networksecuritygroup_ref;
                networkinterface.properties = networkinterface_properties;

                // Add NSG dependsOn to the Network Interface object
                if (!networkinterface.dependsOn.Contains(networksecuritygroup_ref.id))
                {
                    networkinterface.dependsOn.Add(networksecuritygroup_ref.id);
                }

            }

            if (resource.SelectNodes("//ConfigurationSets/ConfigurationSet/PublicIPs").Count > 0)
            {
                BuildPublicIPAddressObject(ref networkinterface);
            }

            networkinterfaces.Add(networkinterface_ref);
            _processedItems.Add("Microsoft.Network/networkInterfaces/" + networkinterface.name, networkinterface.location);
            _resources.Add(networkinterface);
            _logProvider.WriteLog("BuildNetworkInterfaceObject", "Microsoft.Network/networkInterfaces/" + networkinterface.name);

            foreach (XmlNode additionalnetworkinterface in resource.SelectNodes("//ConfigurationSets/ConfigurationSet/NetworkInterfaces/NetworkInterface"))
            {
                subnet_name = additionalnetworkinterface.SelectSingleNode("IPConfigurations/IPConfiguration/SubnetName").InnerText.Replace(" ", "");
                subnet_ref = new Reference();
                subnet_ref.id = "[concat(resourceGroup().id,'/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "/subnets/" + subnet_name + "')]";

                privateIPAllocationMethod = "Dynamic";
                privateIPAddress = null;
                if (additionalnetworkinterface.SelectSingleNode("IPConfigurations/IPConfiguration/StaticVirtualNetworkIPAddress") != null)
                {
                    privateIPAllocationMethod = "Static";
                    privateIPAddress = additionalnetworkinterface.SelectSingleNode("IPConfigurations/IPConfiguration/StaticVirtualNetworkIPAddress").InnerText;
                }

                ipconfiguration_properties = new IpConfiguration_Properties();
                ipconfiguration_properties.privateIPAllocationMethod = privateIPAllocationMethod;
                ipconfiguration_properties.privateIPAddress = privateIPAddress;
                ipconfiguration_properties.subnet = subnet_ref;

                ipconfiguration_name = "ipconfig1";
                ipconfiguration = new IpConfiguration();
                ipconfiguration.name = ipconfiguration_name;
                ipconfiguration.properties = ipconfiguration_properties;

                ipConfigurations = new List<IpConfiguration>();
                ipConfigurations.Add(ipconfiguration);

                networkinterface_properties = new NetworkInterface_Properties();
                networkinterface_properties.ipConfigurations = ipConfigurations;
                if (additionalnetworkinterface.SelectNodes("IPForwarding").Count > 0)
                {
                    networkinterface_properties.enableIPForwarding = true;
                }

                dependson = new List<string>();
                if (GetProcessedItem("Microsoft.Network/virtualNetworks/" + virtualnetworkname))
                {
                    dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/virtualNetworks/" + virtualnetworkname + "')]");
                }

                networkinterface_name = virtualmachinename + "-" + additionalnetworkinterface.SelectSingleNode("Name").InnerText;
                networkinterface = new NetworkInterface();
                networkinterface.name = networkinterface_name;
                networkinterface.location = virtualmachineinfo["location"].ToString();
                networkinterface.properties = networkinterface_properties;
                networkinterface.dependsOn = dependson;

                networkinterface_ref_properties = new NetworkProfile_NetworkInterface_Properties();
                networkinterface_ref_properties.primary = false;

                networkinterface_ref = new NetworkProfile_NetworkInterface();
                networkinterface_ref.id = "[concat(resourceGroup().id, '/providers/Microsoft.Network/networkInterfaces/" + networkinterface.name + "')]";
                networkinterface_ref.properties = networkinterface_ref_properties;

                networkinterfaces.Add(networkinterface_ref);
                _processedItems.Add("Microsoft.Network/networkInterfaces/" + networkinterface.name, networkinterface.location);
                _resources.Add(networkinterface);
                _logProvider.WriteLog("BuildNetworkInterfaceObject", "Microsoft.Network/networkInterfaces/" + networkinterface.name);
            }

            _logProvider.WriteLog("BuildNetworkInterfaceObject", "End");
        }

        private void BuildVirtualMachineObject(string subscriptionId, XmlNode resource, Hashtable virtualmachineinfo, List<NetworkProfile_NetworkInterface> networkinterfaces, string token)
        {
            _logProvider.WriteLog("BuildVirtualMachineObject", "Start");

            string virtualmachinename = virtualmachineinfo["virtualmachinename"].ToString();
            string networkinterfacename = virtualmachinename;
            string ostype = resource.SelectSingleNode("//OSVirtualHardDisk/OS").InnerText;

            XmlNode osvirtualharddisk = resource.SelectSingleNode("//OSVirtualHardDisk");
            string olddiskurl = osvirtualharddisk.SelectSingleNode("MediaLink").InnerText;
            string[] splitarray = olddiskurl.Split(new char[] { '/' });
            string oldstorageaccountname = splitarray[2].Split(new char[] { '.' })[0];
            string newstorageaccountname = oldstorageaccountname + app.Default.UniquenessSuffix;
            string newdiskurl = olddiskurl.Replace(oldstorageaccountname + ".", newstorageaccountname + ".");

            Hashtable storageaccountdependencies = new Hashtable();
            storageaccountdependencies.Add(newstorageaccountname, "");

            HardwareProfile hardwareprofile = new HardwareProfile();
            hardwareprofile.vmSize = GetVMSize(resource.SelectSingleNode("//RoleSize").InnerText);

            NetworkProfile networkprofile = new NetworkProfile();
            networkprofile.networkInterfaces = networkinterfaces;

            Vhd vhd = new Vhd();
            vhd.uri = newdiskurl;

            OsDisk osdisk = new OsDisk();
            osdisk.name = resource.SelectSingleNode("//OSVirtualHardDisk/DiskName").InnerText;
            osdisk.vhd = vhd;
            osdisk.caching = resource.SelectSingleNode("//OSVirtualHardDisk/HostCaching").InnerText;

            ImageReference imagereference = new ImageReference();
            OsProfile osprofile = new OsProfile();

            // if the tool is configured to create new VMs with empty data disks
            if (app.Default.BuildEmpty)
            {
                osdisk.createOption = "FromImage";

                osprofile.computerName = virtualmachinename;
                osprofile.adminUsername = "[parameters('adminUsername')]";
                osprofile.adminPassword = "[parameters('adminPassword')]";

                if (!_parameters.ContainsKey("adminUsername"))
                {
                    Parameter parameter = new Parameter();
                    parameter.type = "string";
                    _parameters.Add("adminUsername", parameter);
                }

                if (!_parameters.ContainsKey("adminPassword"))
                {
                    Parameter parameter = new Parameter();
                    parameter.type = "securestring";
                    _parameters.Add("adminPassword", parameter);
                }

                if (ostype == "Windows")
                {
                    imagereference.publisher = "MicrosoftWindowsServer";
                    imagereference.offer = "WindowsServer";
                    imagereference.sku = "2012-R2-Datacenter";
                    imagereference.version = "latest";
                }
                else if (ostype == "Linux")
                {
                    imagereference.publisher = "Canonical";
                    imagereference.offer = "UbuntuServer";
                    imagereference.sku = "16.04.0-LTS";
                    imagereference.version = "latest";
                }
                else
                {
                    imagereference.publisher = "<publisher>";
                    imagereference.offer = "<offer>";
                    imagereference.sku = "<sku>";
                    imagereference.version = "<version>";
                }
            }
            // if the tool is configured to attach copied disks
            else
            {
                osdisk.createOption = "Attach";
                osdisk.osType = ostype;

                // Block of code to help copying the blobs to the new storage accounts
                Hashtable storageaccountinfo = new Hashtable();
                storageaccountinfo.Add("name", oldstorageaccountname);

                XmlDocument storageaccountkeys = _asmRetriever.GetAzureASMResources("StorageAccountKeys", subscriptionId, storageaccountinfo, token);
                string key = storageaccountkeys.SelectSingleNode("//StorageServiceKeys/Primary").InnerText;

                CopyBlobDetail copyblobdetail = new CopyBlobDetail();
                copyblobdetail.SourceSA = oldstorageaccountname;
                copyblobdetail.SourceContainer = splitarray[3];
                copyblobdetail.SourceBlob = splitarray[4];
                copyblobdetail.SourceKey = key;
                copyblobdetail.DestinationSA = newstorageaccountname;
                copyblobdetail.DestinationContainer = splitarray[3];
                copyblobdetail.DestinationBlob = splitarray[4];
                _copyBlobDetails.Add(copyblobdetail);
                // end of block of code to help copying the blobs to the new storage accounts
            }

            // process data disks
            List<DataDisk> datadisks = new List<DataDisk>();
            XmlNodeList datadisknodes = resource.SelectNodes("//DataVirtualHardDisks/DataVirtualHardDisk");
            foreach (XmlNode datadisknode in datadisknodes)
            {
                DataDisk datadisk = new DataDisk();
                datadisk.name = datadisknode.SelectSingleNode("DiskName").InnerText;
                datadisk.caching = datadisknode.SelectSingleNode("HostCaching").InnerText;
                datadisk.diskSizeGB = Int64.Parse(datadisknode.SelectSingleNode("LogicalDiskSizeInGB").InnerText);

                datadisk.lun = 0;
                if (datadisknode.SelectSingleNode("Lun") != null)
                {
                    datadisk.lun = Int64.Parse(datadisknode.SelectSingleNode("Lun").InnerText);
                }

                olddiskurl = datadisknode.SelectSingleNode("MediaLink").InnerText;
                splitarray = olddiskurl.Split(new char[] { '/' });
                oldstorageaccountname = splitarray[2].Split(new char[] { '.' })[0];
                newstorageaccountname = oldstorageaccountname + app.Default.UniquenessSuffix;
                newdiskurl = olddiskurl.Replace(oldstorageaccountname + ".", newstorageaccountname + ".");

                // if the tool is configured to create new VMs with empty data disks
                if (app.Default.BuildEmpty)
                {
                    datadisk.createOption = "Empty";
                }
                // if the tool is configured to attach copied disks
                else
                {
                    datadisk.createOption = "Attach";

                    // Block of code to help copying the blobs to the new storage accounts
                    Hashtable storageaccountinfo = new Hashtable();
                    storageaccountinfo.Add("name", oldstorageaccountname);

                    XmlDocument storageaccountkeys = _asmRetriever.GetAzureASMResources("StorageAccountKeys", subscriptionId, storageaccountinfo, token);
                    string key = storageaccountkeys.SelectSingleNode("//StorageServiceKeys/Primary").InnerText;

                    CopyBlobDetail copyblobdetail = new CopyBlobDetail();
                    copyblobdetail.SourceSA = oldstorageaccountname;
                    copyblobdetail.SourceContainer = splitarray[3];
                    copyblobdetail.SourceBlob = splitarray[4];
                    copyblobdetail.SourceKey = key;
                    copyblobdetail.DestinationSA = newstorageaccountname;
                    copyblobdetail.DestinationContainer = splitarray[3];
                    copyblobdetail.DestinationBlob = splitarray[4];
                    _copyBlobDetails.Add(copyblobdetail);
                    // end of block of code to help copying the blobs to the new storage accounts
                }

                vhd = new Vhd();
                vhd.uri = newdiskurl;
                datadisk.vhd = vhd;

                try { storageaccountdependencies.Add(newstorageaccountname, ""); }
                catch { }

                datadisks.Add(datadisk);
            }

            StorageProfile storageprofile = new StorageProfile();
            if (app.Default.BuildEmpty) { storageprofile.imageReference = imagereference; }
            storageprofile.osDisk = osdisk;
            storageprofile.dataDisks = datadisks;

            VirtualMachine_Properties virtualmachine_properties = new VirtualMachine_Properties();
            virtualmachine_properties.hardwareProfile = hardwareprofile;
            if (app.Default.BuildEmpty) { virtualmachine_properties.osProfile = osprofile; }
            virtualmachine_properties.networkProfile = networkprofile;
            virtualmachine_properties.storageProfile = storageprofile;

            List<string> dependson = new List<string>();
            dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Network/networkInterfaces/" + networkinterfacename + "')]");

            // Diagnostics Extension
            Extension extension_iaasdiagnostics = null;

            //XmlNodeList resourceextensionreferences = resource.SelectNodes("//ResourceExtensionReferences/ResourceExtensionReference");
            //foreach (XmlNode resourceextensionreference in resourceextensionreferences)
            //{
            //    if (resourceextensionreference.SelectSingleNode("Name").InnerText == "IaaSDiagnostics")
            //    {
            //        string json = Base64Decode(resourceextensionreference.SelectSingleNode("ResourceExtensionParameterValues/ResourceExtensionParameterValue/Value").InnerText);
            //        var resourceextensionparametervalue = JsonConvert.DeserializeObject<dynamic>(json);
            //        string diagnosticsstorageaccount = resourceextensionparametervalue.storageAccount.Value + app.Default.UniquenessSuffix;
            //        string xmlcfgvalue = Base64Decode(resourceextensionparametervalue.xmlCfg.Value);
            //        xmlcfgvalue = xmlcfgvalue.Replace("\n", "");
            //        xmlcfgvalue = xmlcfgvalue.Replace("\r", "");

            //        XmlDocument xmlcfg = new XmlDocument();
            //        xmlcfg.LoadXml(xmlcfgvalue);

            //        XmlNodeList mynodelist = xmlcfg.SelectNodes("/wadCfg/DiagnosticMonitorConfiguration/Metrics");


                    

            //        extension_iaasdiagnostics = new Extension();
            //        extension_iaasdiagnostics.name = "Microsoft.Insights.VMDiagnosticsSettings";
            //        extension_iaasdiagnostics.type = "extensions";
            //        extension_iaasdiagnostics.location = virtualmachineinfo["location"].ToString();
            //        extension_iaasdiagnostics.dependsOn = new List<string>();
            //        extension_iaasdiagnostics.dependsOn.Add("[concat(resourceGroup().id, '/providers/Microsoft.Compute/virtualMachines/" + virtualmachinename + "')]");
            //        extension_iaasdiagnostics.dependsOn.Add("[concat(resourceGroup().id, '/providers/Microsoft.Storage/storageAccounts/" + diagnosticsstorageaccount + "')]");

            //        Extension_Properties extension_iaasdiagnostics_properties = new Extension_Properties();
            //        extension_iaasdiagnostics_properties.publisher = "Microsoft.Azure.Diagnostics";
            //        extension_iaasdiagnostics_properties.type = "IaaSDiagnostics";
            //        extension_iaasdiagnostics_properties.typeHandlerVersion = "1.5";
            //        extension_iaasdiagnostics_properties.autoUpgradeMinorVersion = true;
            //        extension_iaasdiagnostics_properties.settings = new Dictionary<string, string>();
            //        extension_iaasdiagnostics_properties.settings.Add("xmlCfg", "[base64('" + xmlcfgvalue + "')]");
            //        extension_iaasdiagnostics_properties.settings.Add("storageAccount", diagnosticsstorageaccount);
            //        extension_iaasdiagnostics.properties = new Extension_Properties();
            //        extension_iaasdiagnostics.properties = extension_iaasdiagnostics_properties;
            //    }
            //}
            
            // Availability Set
            string availabilitysetname = virtualmachineinfo["cloudservicename"] + "-defaultAS";
            if (resource.SelectSingleNode("//AvailabilitySetName") != null)
            {
                availabilitysetname = resource.SelectSingleNode("//AvailabilitySetName").InnerText;
            }

            Reference availabilityset = new Reference();
            availabilityset.id = "[concat(resourceGroup().id, '/providers/Microsoft.Compute/availabilitySets/" + availabilitysetname + "')]";
            virtualmachine_properties.availabilitySet = availabilityset;

            dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Compute/availabilitySets/" + availabilitysetname + "')]");

            foreach (DictionaryEntry storageaccountdependency in storageaccountdependencies)
            {
                if (GetProcessedItem("Microsoft.Storage/storageAccounts/" + storageaccountdependency.Key))
                {
                    dependson.Add("[concat(resourceGroup().id, '/providers/Microsoft.Storage/storageAccounts/" + storageaccountdependency.Key + "')]");
                }
            }

            VirtualMachine virtualmachine = new VirtualMachine();
            virtualmachine.name = virtualmachinename;
            virtualmachine.location = virtualmachineinfo["location"].ToString();
            virtualmachine.properties = virtualmachine_properties;
            virtualmachine.dependsOn = dependson;
            virtualmachine.resources = new List<Resource>();
            if (extension_iaasdiagnostics != null) { virtualmachine.resources.Add(extension_iaasdiagnostics); }

            _processedItems.Add("Microsoft.Compute/virtualMachines/" + virtualmachine.name, virtualmachine.location);
            _resources.Add(virtualmachine);
            _logProvider.WriteLog("BuildVirtualMachineObject", "Microsoft.Compute/virtualMachines/" + virtualmachine.name);

            _logProvider.WriteLog("BuildVirtualMachineObject", "End");
        }

        private void BuildStorageAccountObject(XmlNode resource)
        {
            _logProvider.WriteLog("BuildStorageAccountObject", "Start");

            StorageAccount_Properties storageaccount_properties = new StorageAccount_Properties();
            storageaccount_properties.accountType = resource.SelectSingleNode("//StorageServiceProperties/AccountType").InnerText;

            StorageAccount storageaccount = new StorageAccount();
            storageaccount.name = resource.SelectSingleNode("//ServiceName").InnerText + app.Default.UniquenessSuffix;
            storageaccount.location = resource.SelectSingleNode("//ExtendedProperties/ExtendedProperty[Name='ResourceLocation']/Value").InnerText;
            storageaccount.properties = storageaccount_properties;

            _processedItems.Add("Microsoft.Storage/storageAccounts/" + storageaccount.name, storageaccount.location);
            _resources.Add(storageaccount);
            _logProvider.WriteLog("BuildStorageAccountObject", "Microsoft.Storage/storageAccounts/" + storageaccount.name);

            _logProvider.WriteLog("BuildStorageAccountObject", "End");
        }

        private void WriteStream(StreamWriter writer, string text)
        {
            writer.Write(text);
            writer.Close();
        }

        private bool GetProcessedItem(string processeditem)
        {
            if (_processedItems.ContainsKey(processeditem))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        

        // convert an hex string into byte array
        public static byte[] StrToByteArray(string str)
        {
            Dictionary<string, byte> hexindex = new Dictionary<string, byte>();
            for (int i = 0; i <= 255; i++)
                hexindex.Add(i.ToString("X2"), (byte)i);

            List<byte> hexres = new List<byte>();
            for (int i = 0; i < str.Length; i += 2)
                hexres.Add(hexindex[str.Substring(i, 2)]);

            return hexres.ToArray();
        }

        public static string Base64Decode(string base64EncodedData)
        {
            byte[] base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        public static string Base64Encode(string plainText)
        {
            byte[] plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        private string GetVMSize(string vmsize)
        {
            Dictionary<string, string> VMSizeTable = new Dictionary<string, string>();
            VMSizeTable.Add("ExtraSmall", "Standard_A0");
            VMSizeTable.Add("Small", "Standard_A1");
            VMSizeTable.Add("Medium", "Standard_A2");
            VMSizeTable.Add("Large", "Standard_A3");
            VMSizeTable.Add("ExtraLarge", "Standard_A4");
            VMSizeTable.Add("A5", "Standard_A5");
            VMSizeTable.Add("A6", "Standard_A6");
            VMSizeTable.Add("A7", "Standard_A7");
            VMSizeTable.Add("A8", "Standard_A8");
            VMSizeTable.Add("A9", "Standard_A9");
            VMSizeTable.Add("A10", "Standard_A10");
            VMSizeTable.Add("A11", "Standard_A11");

            if (VMSizeTable.ContainsKey(vmsize))
            {
                return VMSizeTable[vmsize];
            }
            else
            {
                return vmsize;
            }
        }
    }
}
