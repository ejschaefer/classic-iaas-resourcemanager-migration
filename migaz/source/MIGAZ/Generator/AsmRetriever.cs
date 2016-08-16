using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace MIGAZ.Generator
{
    public class AsmRetriever
    {
        private ILogProvider _logProvider;
        private IStatusProvider _statusProvider;

        public Dictionary<string, XmlDocument> _documentCache = new Dictionary<string, XmlDocument>();

        public AsmRetriever(ILogProvider logProvider, IStatusProvider statusProvider)
        {
            _logProvider = logProvider;
            _statusProvider = statusProvider;
        }
        public virtual XmlDocument GetAzureASMResources(string resourceType, string subscriptionId, Hashtable info, string token)
        {
            _logProvider.WriteLog("GetAzureASMResources", "Start");

            string url = null;
            switch (resourceType)
            {
                case "Subscriptions":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + "subscriptions";
                    _statusProvider.UpdateStatus("BUSY: Getting Subscriptions...");
                    break; 
                case "VirtualNetworks":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/virtualnetwork";
                    _statusProvider.UpdateStatus("BUSY: Getting Virtual Networks for Subscription ID : " + subscriptionId + "...");
                    break;
                case "ClientRootCertificates":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/clientrootcertificates";
                    _statusProvider.UpdateStatus("BUSY: Getting Client Root Certificates for Virtual Network : " + info["virtualnetworkname"] + "...");
                    break;
                case "ClientRootCertificate":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/clientrootcertificates/" + info["thumbprint"];
                    _statusProvider.UpdateStatus("BUSY: Getting certificate data for certificate : " + info["thumbprint"] + "...");
                    break;
                case "NetworkSecurityGroup":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/networksecuritygroups/" + info["name"] + "?detaillevel=Full";
                    _statusProvider.UpdateStatus("BUSY: Getting Network Security Group : " + info["name"] + "...");
                    break;
                case "RouteTable":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/routetables/" + info["name"] + "?detailLevel=full";
                    _statusProvider.UpdateStatus("BUSY: Getting Route Table : " + info["routetablename"] + "...");
                    break;
                case "NSGSubnet":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/virtualnetwork/" + info["virtualnetworkname"] + "/subnets/" + info["subnetname"] + "/networksecuritygroups";
                    _statusProvider.UpdateStatus("BUSY: Getting NSG for subnet " + info["subnetname"] + "...");
                    break;
                case "VirtualNetworkGateway":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway";
                    _statusProvider.UpdateStatus("BUSY: Getting Virtual Network Gateway : " + info["virtualnetworkname"] + "...");
                    break;
                case "VirtualNetworkGatewaySharedKey":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/" + info["virtualnetworkname"] + "/gateway/connection/" + info["localnetworksitename"] + "/sharedkey";
                    _statusProvider.UpdateStatus("BUSY: Getting Virtual Network Gateway Shared Key: " + info["localnetworksitename"] + "...");
                    break;
                case "StorageAccounts":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/storageservices";
                    _statusProvider.UpdateStatus("BUSY: Getting Storage Accounts for Subscription ID : " + subscriptionId + "...");
                    break;
                case "StorageAccount":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/storageservices/" + info["name"];
                    _statusProvider.UpdateStatus("BUSY: Getting Storage Accounts for Subscription ID : " + subscriptionId + "...");
                    break;
                case "StorageAccountKeys":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/storageservices/" + info["name"] + "/keys";
                    _statusProvider.UpdateStatus("BUSY: Getting Storage Accounts for Subscription ID : " + subscriptionId + "...");
                    break;
                case "CloudServices":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/hostedservices";
                    _statusProvider.UpdateStatus("BUSY: Getting Cloud Services for Subscription ID : " + subscriptionId + "...");
                    break;
                case "CloudService":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/hostedservices/" + info["name"] + "?embed-detail=true";
                    _statusProvider.UpdateStatus("BUSY: Getting Virtual Machines for Cloud Service : " + info["name"] + "...");
                    break;
                case "VirtualMachine":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/hostedservices/" + info["cloudservicename"] + "/deployments/" + info["deploymentname"] + "/roles/" + info["virtualmachinename"];
                    _statusProvider.UpdateStatus("BUSY: Getting Virtual Machines for Cloud Service : " + info["virtualmachinename"] + "...");
                    break;
                case "VMImages":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/images";
                    break;
                case "ReservedIPs":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/services/networking/reservedips";
                    break;
                case "AffinityGroup":
                    url = ServiceUrls.GetServiceManagementUrl(app.Default.AzureEnvironment) + subscriptionId + "/affinitygroups/" + info["affinitygroupname"];
                    break;
            }

            Application.DoEvents();

            _logProvider.WriteLog("GetAzureASMResources", "GET " + url);

            if (_documentCache.ContainsKey(url))
            {
                _logProvider.WriteLog("GetAzureASMResources", "FROM XML CACHE");
                _logProvider.WriteLog("GetAzureASMResources", "End");
                writeXMLtoFile(url, "Cached");
                return _documentCache[url];
            }

            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
            request.Headers.Add(HttpRequestHeader.Authorization, "Bearer " + token);
            request.Headers.Add("x-ms-version", "2015-04-01");
            request.Method = "GET";


            string xml = "";
            try
            {
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                xml = new StreamReader(response.GetResponseStream()).ReadToEnd();
                _logProvider.WriteLog("GetAzureASMResources", "RESPONSE " + response.StatusCode);
            }
            catch (Exception exception)
            {
                _logProvider.WriteLog("GetAzureASMResources", "EXCEPTION " + exception.Message);
                DialogResult dialogresult = MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                xml = "";
                //Application.ExitThread();
            }

            if (xml != "")
            {
                XmlDocument xmlDoc = RemoveXmlns(xml);

                _logProvider.WriteLog("GetAzureASMResources", "End");
                writeXMLtoFile(url, xml);

                _documentCache.Add(url, xmlDoc);
                return xmlDoc;
            }
            else
            {
                //XmlNodeList xmlnode = null;
                //return xmlnode;
                XmlDocument xmlDoc = new XmlDocument();

                _logProvider.WriteLog("GetAzureASMResources", "End");
                writeXMLtoFile(url, "");
                return null;
            }

        }

        protected XmlDocument RemoveXmlns(String xml)
        {
            if (!xml.StartsWith("<"))
            {
                xml = $"<data>{xml}</data>"; 
            }
            XDocument d = XDocument.Parse(xml);
            d.Root.DescendantsAndSelf().Attributes().Where(x => x.IsNamespaceDeclaration).Remove();

            foreach (var elem in d.Descendants())
                elem.Name = elem.Name.LocalName;

            var xmlDocument = new XmlDocument();
            xmlDocument.Load(d.CreateReader());

            return xmlDocument;
        }

        private void writeXMLtoFile(string url, string xml)
        {
            string logfilepath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\MIGAZ\\MIGAZ-XML-" + string.Format("{0:yyyyMMdd}", DateTime.Now) + ".log";
            string text = DateTime.Now.ToString() + "   " + url + Environment.NewLine;
            File.AppendAllText(logfilepath, text);
            text = xml + Environment.NewLine;
            File.AppendAllText(logfilepath, text);
            text = Environment.NewLine;
            File.AppendAllText(logfilepath, text);
        }

        public virtual void GetVMDetails(string subscriptionId, string cloudServiceName, string virtualMachineName, string token, out string deploymentName, out string virtualNetworkName, out string loadBalancerName)
        {
            Hashtable cloudserviceinfo = new Hashtable();
            cloudserviceinfo.Add("name", cloudServiceName);

            XmlDocument hostedservice = GetAzureASMResources("CloudService", subscriptionId, cloudserviceinfo, token);
            if (hostedservice.SelectNodes("//Deployments/Deployment").Count > 0)
            {
                if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role")[0].SelectNodes("RoleType").Count > 0)
                {
                    if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role")[0].SelectSingleNode("RoleType").InnerText == "PersistentVMRole")
                    {
                        virtualNetworkName = "empty";
                        if (hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("VirtualNetworkName") != null)
                        {
                            virtualNetworkName = hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("VirtualNetworkName").InnerText;
                        }
                        deploymentName = hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectSingleNode("Name").InnerText;
                        XmlNodeList roles = hostedservice.SelectNodes("//Deployments/Deployment")[0].SelectNodes("RoleList/Role");
                        // GetVMLBMapping is necessary because a Cloud Service can have multiple availability sets
                        // On ARM, a load balancer can only be attached to 1 availability set
                        // Because of this, if multiple availability sets exist, we are breaking the cloud service in multiple load balancers
                        //     to respect all availability sets
                        Dictionary<string, string> vmlbmapping = GetVMLBMapping(cloudServiceName, roles);
                        foreach (XmlNode role in roles)
                        {
                            string currentVM = role.SelectSingleNode("RoleName").InnerText;
                            if (currentVM == virtualMachineName)
                            {
                                loadBalancerName = vmlbmapping[virtualMachineName];
                                return;
                            }
                        }
                    }
                }
            }
            throw new InvalidOperationException("Requested VM could not be found");
        }

        public Dictionary<string, string> GetVMLBMapping(string cloudservicename, XmlNodeList roles)
        {
            Dictionary<string, string> aslbnamemapping = new Dictionary<string, string>();
            Dictionary<string, string> aslbnamemapping2 = new Dictionary<string, string>();

            foreach (XmlNode role in roles)
            {
                string availabilitysetname = "empty";
                if (role.SelectNodes("AvailabilitySetName").Count > 0)
                {
                    availabilitysetname = role.SelectSingleNode("AvailabilitySetName").InnerText;
                }

                if (!aslbnamemapping.ContainsKey(availabilitysetname))
                {
                    aslbnamemapping.Add(availabilitysetname, "");
                }
            }

            if (aslbnamemapping.Count == 1)
            {
                foreach (KeyValuePair<string, string> keyvaluepair in aslbnamemapping)
                {
                    aslbnamemapping2.Add(keyvaluepair.Key, cloudservicename);
                }
            }
            else
            {
                int increment = 1;
                foreach (KeyValuePair<string, string> keyvaluepair in aslbnamemapping)
                {
                    aslbnamemapping2.Add(keyvaluepair.Key, cloudservicename + "-LB" + increment.ToString());
                    increment += 1;
                }
            }

            Dictionary<string, string> vmlbmapping = new Dictionary<string, string>();

            foreach (XmlNode role in roles)
            {
                string virtualmachinename = role.SelectSingleNode("RoleName").InnerText;
                string availabilitysetname = "empty";
                if (role.SelectNodes("AvailabilitySetName").Count > 0)
                {
                    availabilitysetname = role.SelectSingleNode("AvailabilitySetName").InnerText;
                }
                string loadbalancername = aslbnamemapping2[availabilitysetname];

                vmlbmapping.Add(virtualmachinename, loadbalancername);
            }

            return vmlbmapping;
        }
    }
}
